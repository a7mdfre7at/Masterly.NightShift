namespace Masterly.NightShift.Redis;

/// <summary>
/// The Lua scripts of the Redis storage. Each takes the key prefix as its only key, so it runs on the node that
/// holds the prefix's hash slot, and builds the other keys from it. Every key shares the prefix's hash tag.
/// </summary>
internal static class RedisScripts
{
    /// <summary>
    /// Creates a job: ARGV = payload, arguments, created-at ticks, expire-at ticks, time to live (ms),
    /// expire-at (Unix ms), parameter count, then name/value pairs. Returns the new id.
    /// </summary>
    public const string CreateJob = """
        local p = KEYS[1]
        local id = redis.call('INCR', p .. 'job-id')
        local jobKey = p .. 'job:' .. id
        redis.call('HSET', jobKey, 'InvocationData', ARGV[1], 'Arguments', ARGV[2], 'CreatedAt', ARGV[3], 'ExpireAt', ARGV[4])
        redis.call('PEXPIRE', jobKey, ARGV[5])
        local n = tonumber(ARGV[7])
        if n > 0 then
          local fields = {}
          for i = 1, n * 2 do fields[i] = ARGV[7 + i] end
          redis.call('HSET', jobKey .. ':params', unpack(fields))
          redis.call('PEXPIRE', jobKey .. ':params', ARGV[5])
        end
        redis.call('ZADD', p .. 'jobs:expiring', ARGV[6], id)
        return id
        """;

    /// <summary>
    /// Applies a transaction's operations in order, all at once. ARGV holds the operations one after another,
    /// each an opcode followed by its arguments.
    /// </summary>
    public const string Apply = """
        local p = KEYS[1]
        local a = ARGV
        local i = 1
        local woke = {}

        local function inherit(key, jobKey)
          local ttl = redis.call('PTTL', jobKey)
          if ttl > 0 then redis.call('PEXPIRE', key, ttl) end
        end

        local function keyOf(kind, key)
          return p .. kind .. ':' .. key
        end

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
                redis.call('ZADD', p .. 'jobs:expiring', a[i + 4], a[i + 1])
              end
            end
            i = i + 5
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
            for j = 1, n do redis.call('ZADD', keyOf('set', a[i + 1]), 0, a[i + 2 + j]) end
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
              redis.call('HSET', keyOf('hash', a[i + 1]), unpack(fields))
            end
            i = i + 3 + n * 2
          elseif op == 'expire' then
            local key = keyOf(a[i + 1], a[i + 2])
            if a[i + 3] == '' then redis.call('PERSIST', key) else redis.call('PEXPIRE', key, a[i + 3]) end
            i = i + 4
          else
            return redis.error_reply('Unknown operation ' .. tostring(op))
          end
        end

        for queue, _ in pairs(woke) do redis.call('PUBLISH', p .. 'enqueued', queue) end
        return 1
        """;

    /// <summary>
    /// Takes the oldest entry of the first queue that has one, after putting back the entries whose fetchers
    /// stopped keeping them alive. ARGV = now (Unix ms), stale-before (Unix ms), token, then the queues.
    /// Returns { entry, queue }, or nothing.
    /// </summary>
    public const string Fetch = """
        local p = KEYS[1]
        for i = 4, #ARGV do
          local q = ARGV[i]
          local queueKey = p .. 'queue:' .. q
          local fetchedKey = queueKey .. ':fetched'
          local tokensKey = queueKey .. ':tokens'
          local stale = redis.call('ZRANGEBYSCORE', fetchedKey, '-inf', '(' .. ARGV[2])
          for _, entry in ipairs(stale) do
            redis.call('ZREM', fetchedKey, entry)
            redis.call('HDEL', tokensKey, entry)
            redis.call('RPUSH', queueKey, entry)
          end
          local entry = redis.call('RPOP', queueKey)
          if entry then
            redis.call('ZADD', fetchedKey, ARGV[1], entry)
            redis.call('HSET', tokensKey, entry, ARGV[3])
            return { entry, q }
          end
        end
        return false
        """;

    /// <summary>
    /// Acts on a fetched entry if the token still owns it. ARGV = remove | requeue | keepalive, queue, entry,
    /// token, now (Unix ms).
    /// </summary>
    public const string Fetched = """
        local queueKey = KEYS[1] .. 'queue:' .. ARGV[2]
        local fetchedKey = queueKey .. ':fetched'
        local tokensKey = queueKey .. ':tokens'
        if redis.call('HGET', tokensKey, ARGV[3]) ~= ARGV[4] then return 0 end
        if ARGV[1] == 'keepalive' then
          redis.call('ZADD', fetchedKey, 'XX', ARGV[5], ARGV[3])
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
    /// ARGV = now (Unix ms), batch size. Returns the number removed.
    /// </summary>
    public const string RemoveExpiredJobs = """
        local p = KEYS[1]
        local ids = redis.call('ZRANGEBYSCORE', p .. 'jobs:expiring', '-inf', ARGV[1], 'LIMIT', 0, ARGV[2])
        local states = redis.call('SMEMBERS', p .. 'states')
        for _, id in ipairs(ids) do
          local jobKey = p .. 'job:' .. id
          redis.call('DEL', jobKey, jobKey .. ':params', jobKey .. ':history')
          for _, s in ipairs(states) do redis.call('ZREM', p .. 'state:' .. s, id) end
          redis.call('ZREM', p .. 'jobs:expiring', id)
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
