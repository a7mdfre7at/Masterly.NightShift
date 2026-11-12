namespace Masterly.NightShift.Redis;

/// <summary>
/// The Lua scripts of the Redis storage. Each takes the key prefix as its only key, so it runs on the node that
/// holds the prefix's hash slot, and builds the other keys from it. Every key shares the prefix's hash tag.
/// Times that decide something (when a fetched job goes back to its queue, when a job is removed from the state
/// lists, when a server timed out) are read from the Redis server's clock inside the script, so clients whose
/// clocks disagree still agree on them; clients pass durations only.
/// </summary>
internal static class RedisScripts
{
    // Reads the server's clock in milliseconds. Before Redis 7, a script that reads TIME and then writes must
    // replicate its effects rather than itself; Redis 7 always does, and keeps the call for compatibility.
    private const string Now = """
        if redis.replicate_commands then redis.replicate_commands() end
        local clock = redis.call('TIME')
        local now = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)

        """;

    // Calls a command with a fixed head and a long tail of arguments in chunks, because unpack() fails past a few
    // thousand values. The chunks hold an even number of values, so field/value pairs stay together.
    private const string Chunked = """
        local function chunked(command, key, values)
          local size = 1000
          for first = 1, #values, size do
            local last = math.min(first + size - 1, #values)
            redis.call(command, key, unpack(values, first, last))
          end
        end

        """;

    /// <summary>
    /// Creates a job: ARGV = payload, arguments, created-at ticks, expire-at ticks, time to live (ms), parameter
    /// count, then name/value pairs. Returns the new id.
    /// </summary>
    public const string CreateJob = Now + Chunked + """
        local p = KEYS[1]
        local id = redis.call('INCR', p .. 'job-id')
        local jobKey = p .. 'job:' .. id
        redis.call('HSET', jobKey, 'InvocationData', ARGV[1], 'Arguments', ARGV[2], 'CreatedAt', ARGV[3], 'ExpireAt', ARGV[4])
        redis.call('PEXPIRE', jobKey, ARGV[5])
        local n = tonumber(ARGV[6])
        if n > 0 then
          local fields = {}
          for i = 1, n * 2 do fields[i] = ARGV[6 + i] end
          chunked('HSET', jobKey .. ':params', fields)
          redis.call('PEXPIRE', jobKey .. ':params', ARGV[5])
        end
        redis.call('ZADD', p .. 'jobs:expiring', now + tonumber(ARGV[5]), id)
        return id
        """;

    /// <summary>
    /// Applies a transaction's operations in order: ARGV holds the operations one after another, each an opcode
    /// followed by its arguments. A first pass checks every opcode, its arguments, and the type of every
    /// collection key written, and fails before writing anything when one is wrong. Redis doesn't roll a script
    /// back, so the transaction is all or none for input that passes the check; what can still fail half way is
    /// the server itself, such as running out of memory.
    /// </summary>
    public const string Apply = Now + Chunked + """
        local p = KEYS[1]
        local a = ARGV
        local woke = {}

        local fixed = {
          ['expire-job'] = 4, state = 5, queue = 3, dequeue = 4, param = 4, counter = 4, zadd = 4, zrem = 3,
          del = 3, lpush = 3, lrem = 3, ltrim = 4, expire = 4
        }
        local kinds = { set = 'zset', list = 'list', hash = 'hash' }

        local function keyOf(kind, key)
          return p .. kind .. ':' .. key
        end

        local function isInteger(value)
          local number = tonumber(value)
          return number ~= nil and number == math.floor(number)
        end

        local function isTtl(value)
          return value == '' or isInteger(value)
        end

        local function holds(key, expected)
          local found = redis.call('TYPE', key)['ok']
          return found == 'none' or found == expected
        end

        -- Returns nothing when the operation at i is valid, otherwise why not
        local function check(i, op)
          if op == 'expire-job' then
            if not isTtl(a[i + 2]) then return 'a bad time to live' end
          elseif op == 'counter' then
            if not isInteger(a[i + 2]) or not isTtl(a[i + 3]) then return 'a bad value or time to live' end
            if not holds(keyOf('counter', a[i + 1]), 'string') then return 'a key of another type' end
          elseif op == 'zadd' then
            if not tonumber(a[i + 3]) then return 'a bad score' end
            if not holds(keyOf('set', a[i + 1]), 'zset') then return 'a key of another type' end
          elseif op == 'zadd-range' or op == 'zrem' then
            if not holds(keyOf('set', a[i + 1]), 'zset') then return 'a key of another type' end
          elseif op == 'lpush' or op == 'lrem' then
            if not holds(keyOf('list', a[i + 1]), 'list') then return 'a key of another type' end
          elseif op == 'ltrim' then
            if not isInteger(a[i + 2]) or not isInteger(a[i + 3]) then return 'a bad range' end
            if not holds(keyOf('list', a[i + 1]), 'list') then return 'a key of another type' end
          elseif op == 'hset' then
            if not holds(keyOf('hash', a[i + 1]), 'hash') then return 'a key of another type' end
          elseif op == 'del' then
            if not kinds[a[i + 1]] then return 'an unknown kind' end
          elseif op == 'expire' then
            if not kinds[a[i + 1]] or not isTtl(a[i + 3]) then return 'an unknown kind or a bad time to live' end
          end
          return nil
        end

        local i = 1
        while i <= #a do
          local op = a[i]
          local size = fixed[op]
          if op == 'zadd-range' or op == 'hset' then
            local n = tonumber(a[i + 2])
            if not isInteger(a[i + 2]) or n < 0 then return redis.error_reply('Bad count for ' .. op .. ' at argument ' .. i) end
            size = 3 + (op == 'hset' and n * 2 or n)
          end
          if not size then return redis.error_reply('Unknown operation ' .. tostring(op) .. ' at argument ' .. i) end
          if i + size - 1 > #a then return redis.error_reply('Missing arguments for ' .. op .. ' at argument ' .. i) end
          local problem = check(i, op)
          if problem then return redis.error_reply('Operation ' .. op .. ' at argument ' .. i .. ' has ' .. problem) end
          i = i + size
        end

        local function inherit(key, jobKey)
          local ttl = redis.call('PTTL', jobKey)
          if ttl > 0 then redis.call('PEXPIRE', key, ttl) end
        end

        i = 1
        while i <= #a do
          local op = a[i]
          if op == 'expire-job' then
            local jobKey = p .. 'job:' .. a[i + 1]
            if redis.call('EXISTS', jobKey) == 1 then
              local keys = { jobKey, jobKey .. ':params', jobKey .. ':history' }
              if a[i + 2] == '' then
                for _, k in ipairs(keys) do redis.call('PERSIST', k) end
                redis.call('HDEL', jobKey, 'ExpireAt')
                redis.call('ZREM', p .. 'jobs:expiring', a[i + 1])
              else
                for _, k in ipairs(keys) do redis.call('PEXPIRE', k, a[i + 2]) end
                redis.call('HSET', jobKey, 'ExpireAt', a[i + 3])
                redis.call('ZADD', p .. 'jobs:expiring', now + tonumber(a[i + 2]), a[i + 1])
              end
            end
            i = i + 4
          elseif op == 'state' then
            local id = a[i + 1]
            local jobKey = p .. 'job:' .. id
            if redis.call('EXISTS', jobKey) == 1 then
              redis.call('RPUSH', jobKey .. ':history', a[i + 3])
              inherit(jobKey .. ':history', jobKey)
              if a[i + 2] == '1' then
                local old = redis.call('HGET', jobKey, 'StateName')
                if old then redis.call('ZREM', p .. 'state:' .. string.lower(old), id) end
                redis.call('HSET', jobKey, 'StateName', a[i + 4], 'State', a[i + 3])
                local index = string.lower(a[i + 4])
                redis.call('ZADD', p .. 'state:' .. index, id, id)
                redis.call('SADD', p .. 'states', index)
              end
            end
            i = i + 5
          elseif op == 'queue' then
            local entry = redis.call('INCR', p .. 'queue-entry-id') .. ':' .. a[i + 2]
            redis.call('LPUSH', p .. 'queue:' .. a[i + 1], entry)
            redis.call('SADD', p .. 'queues', a[i + 1])
            woke[a[i + 1]] = true
            i = i + 3
          elseif op == 'dequeue' then
            local queueKey = p .. 'queue:' .. a[i + 1]
            if redis.call('HGET', queueKey .. ':tokens', a[i + 2]) == a[i + 3] then
              redis.call('ZREM', queueKey .. ':fetched', a[i + 2])
              redis.call('HDEL', queueKey .. ':tokens', a[i + 2])
            end
            i = i + 4
          elseif op == 'param' then
            local jobKey = p .. 'job:' .. a[i + 1]
            if redis.call('EXISTS', jobKey) == 1 then
              redis.call('HSET', jobKey .. ':params', a[i + 2], a[i + 3])
              inherit(jobKey .. ':params', jobKey)
            end
            i = i + 4
          elseif op == 'counter' then
            local key = keyOf('counter', a[i + 1])
            redis.call('INCRBY', key, a[i + 2])
            if a[i + 3] ~= '' then
              local ttl = redis.call('PTTL', key)
              if ttl < 0 or ttl < tonumber(a[i + 3]) then redis.call('PEXPIRE', key, a[i + 3]) end
            end
            i = i + 4
          elseif op == 'zadd' then
            redis.call('ZADD', keyOf('set', a[i + 1]), a[i + 3], a[i + 2])
            i = i + 4
          elseif op == 'zadd-range' then
            local n = tonumber(a[i + 2])
            local members = {}
            for j = 1, n do
              members[#members + 1] = 0
              members[#members + 1] = a[i + 2 + j]
            end
            if n > 0 then chunked('ZADD', keyOf('set', a[i + 1]), members) end
            i = i + 3 + n
          elseif op == 'zrem' then
            redis.call('ZREM', keyOf('set', a[i + 1]), a[i + 2])
            i = i + 3
          elseif op == 'del' then
            redis.call('DEL', keyOf(a[i + 1], a[i + 2]))
            i = i + 3
          elseif op == 'lpush' then
            redis.call('LPUSH', keyOf('list', a[i + 1]), a[i + 2])
            i = i + 3
          elseif op == 'lrem' then
            redis.call('LREM', keyOf('list', a[i + 1]), 0, a[i + 2])
            i = i + 3
          elseif op == 'ltrim' then
            redis.call('LTRIM', keyOf('list', a[i + 1]), a[i + 2], a[i + 3])
            i = i + 4
          elseif op == 'hset' then
            local n = tonumber(a[i + 2])
            if n > 0 then
              local fields = {}
              for j = 1, n * 2 do fields[j] = a[i + 2 + j] end
              chunked('HSET', keyOf('hash', a[i + 1]), fields)
            end
            i = i + 3 + n * 2
          elseif op == 'expire' then
            local key = keyOf(a[i + 1], a[i + 2])
            if a[i + 3] == '' then redis.call('PERSIST', key) else redis.call('PEXPIRE', key, a[i + 3]) end
            i = i + 4
          end
        end

        for queue, _ in pairs(woke) do redis.call('PUBLISH', p .. 'enqueued', queue) end
        return 1
        """;

    /// <summary>
    /// Takes the oldest entry of the first queue that has one, after putting back the entries whose fetchers
    /// stopped keeping them alive for longer than the invisibility timeout. ARGV = invisibility timeout (ms),
    /// token, then the queues. Returns { entry, queue }, or nothing.
    /// </summary>
    public const string Fetch = Now + """
        local p = KEYS[1]
        local staleBefore = now - tonumber(ARGV[1])
        for i = 3, #ARGV do
          local q = ARGV[i]
          local queueKey = p .. 'queue:' .. q
          local fetchedKey = queueKey .. ':fetched'
          local tokensKey = queueKey .. ':tokens'
          local stale = redis.call('ZRANGEBYSCORE', fetchedKey, '-inf', '(' .. staleBefore)
          for _, entry in ipairs(stale) do
            redis.call('ZREM', fetchedKey, entry)
            redis.call('HDEL', tokensKey, entry)
            redis.call('RPUSH', queueKey, entry)
          end
          local entry = redis.call('RPOP', queueKey)
          if entry then
            redis.call('ZADD', fetchedKey, now, entry)
            redis.call('HSET', tokensKey, entry, ARGV[2])
            return { entry, q }
          end
        end
        return false
        """;

    /// <summary>
    /// Acts on a fetched entry if the token still owns it. ARGV = remove | requeue | keepalive, queue, entry,
    /// token. Returns 1 when it did, 0 when another fetch owns the entry now.
    /// </summary>
    public const string Fetched = Now + """
        local queueKey = KEYS[1] .. 'queue:' .. ARGV[2]
        local fetchedKey = queueKey .. ':fetched'
        local tokensKey = queueKey .. ':tokens'
        if redis.call('HGET', tokensKey, ARGV[3]) ~= ARGV[4] then return 0 end
        if ARGV[1] == 'keepalive' then
          redis.call('ZADD', fetchedKey, 'XX', now, ARGV[3])
          return 1
        end
        redis.call('ZREM', fetchedKey, ARGV[3])
        redis.call('HDEL', tokensKey, ARGV[3])
        if ARGV[1] == 'requeue' then
          redis.call('RPUSH', queueKey, ARGV[3])
          redis.call('PUBLISH', KEYS[1] .. 'enqueued', ARGV[2])
        end
        return 1
        """;

    /// <summary>Reads the hashes of jobs: ARGV = ids. Returns id, fields, id, fields... for the jobs that exist.</summary>
    public const string ReadJobs = """
        local result = {}
        for i = 1, #ARGV do
          local fields = redis.call('HGETALL', KEYS[1] .. 'job:' .. ARGV[i])
          if #fields > 0 then
            result[#result + 1] = ARGV[i]
            result[#result + 1] = fields
          end
        end
        return result
        """;

    /// <summary>Reads a job with its parameters and history: ARGV = id. Returns { fields, parameters, history }.</summary>
    public const string ReadJobDetails = """
        local jobKey = KEYS[1] .. 'job:' .. ARGV[1]
        return {
          redis.call('HGETALL', jobKey),
          redis.call('HGETALL', jobKey .. ':params'),
          redis.call('LRANGE', jobKey .. ':history', 0, -1)
        }
        """;

    /// <summary>
    /// Removes jobs whose time has come, with their parameters, history and state index entries.
    /// ARGV = batch size. Returns the number removed.
    /// </summary>
    public const string RemoveExpiredJobs = Now + """
        local p = KEYS[1]
        local ids = redis.call('ZRANGEBYSCORE', p .. 'jobs:expiring', '-inf', now, 'LIMIT', 0, ARGV[1])
        local states = redis.call('SMEMBERS', p .. 'states')
        for _, id in ipairs(ids) do
          local jobKey = p .. 'job:' .. id
          redis.call('DEL', jobKey, jobKey .. ':params', jobKey .. ':history')
          for _, s in ipairs(states) do redis.call('ZREM', p .. 'state:' .. s, id) end
          redis.call('ZREM', p .. 'jobs:expiring', id)
        end
        return #ids
        """;

    /// <summary>Announces a server with its data and a first heartbeat: ARGV = server id, data.</summary>
    public const string AnnounceServer = Now + """
        redis.call('HSET', KEYS[1] .. 'servers', ARGV[1], ARGV[2])
        redis.call('ZADD', KEYS[1] .. 'servers:heartbeat', now, ARGV[1])
        return 1
        """;

    /// <summary>
    /// Records a server's heartbeat if the server is still announced: ARGV = server id. Returns 0 when it was
    /// removed (it timed out), so a heartbeat never brings back a server that is gone.
    /// </summary>
    public const string Heartbeat = Now + """
        if redis.call('HEXISTS', KEYS[1] .. 'servers', ARGV[1]) == 0 then return 0 end
        redis.call('ZADD', KEYS[1] .. 'servers:heartbeat', now, ARGV[1])
        return 1
        """;

    /// <summary>
    /// Removes the servers whose last heartbeat is older than the timeout, at once, so a heartbeat can't land
    /// between finding and removing them: ARGV = timeout (ms). Returns the number removed.
    /// </summary>
    public const string RemoveTimedOutServers = Now + """
        local p = KEYS[1]
        local ids = redis.call('ZRANGEBYSCORE', p .. 'servers:heartbeat', '-inf', '(' .. (now - tonumber(ARGV[1])))
        for _, id in ipairs(ids) do
          redis.call('HDEL', p .. 'servers', id)
          redis.call('ZREM', p .. 'servers:heartbeat', id)
        end
        return #ids
        """;

    /// <summary>Extends a lock's lease if the owner still holds it: KEYS = lock key, ARGV = owner, time to live (ms).</summary>
    public const string RenewLock = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          redis.call('PEXPIRE', KEYS[1], ARGV[2])
          return 1
        end
        return 0
        """;

    /// <summary>Releases a lock if the owner still holds it: KEYS = lock key, ARGV = owner.</summary>
    public const string ReleaseLock = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    /// <summary>The server's clock: { seconds, microseconds }.</summary>
    public const string Time = "return redis.call('TIME')";
}
