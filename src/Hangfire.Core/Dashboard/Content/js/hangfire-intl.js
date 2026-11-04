// Date formatting for the dashboard, built on the browser's Intl APIs instead of moment.js.
// Exposes `HangfireIntl` and registers it as the date adapter of Chart.js 2.x time scales.
(function (global) {
    'use strict';

    var SECOND = 1000;
    var MINUTE = 60 * SECOND;
    var HOUR = 60 * MINUTE;
    var DAY = 24 * HOUR;
    var WEEK = 7 * DAY;

    // Named formats used by the dashboard and its charts
    var FORMATS = {
        time: { hour: 'numeric', minute: '2-digit' },
        timeWithSeconds: { hour: 'numeric', minute: '2-digit', second: '2-digit' },
        shortDate: { year: 'numeric', month: 'short', day: 'numeric' },
        longDate: { year: 'numeric', month: 'long', day: 'numeric' },
        dateTime: { year: 'numeric', month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit' },
        dateTimeWithSeconds: { year: 'numeric', month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' },
        fullDateTime: { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' },
        numericDateTime: { year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' },
        month: { year: 'numeric', month: 'short' },
        year: { year: 'numeric' }
    };

    var locale;
    var formatters = {};
    var relativeFormatter;

    function setLocale(value) {
        locale = undefined;
        formatters = {};
        relativeFormatter = undefined;

        try {
            // Unknown or invalid tags (like "iv" for the invariant culture) fall back to the browser's locale
            if (value && Intl.DateTimeFormat.supportedLocalesOf([value]).length > 0) {
                locale = value;
            }
        } catch (e) {
            locale = undefined;
        }
    }

    function format(time, name) {
        var options = FORMATS[name] || FORMATS.dateTimeWithSeconds;
        var key = FORMATS[name] ? name : 'dateTimeWithSeconds';

        if (!formatters[key]) {
            formatters[key] = new Intl.DateTimeFormat(locale, options);
        }

        return formatters[key].format(new Date(time));
    }

    // Same thresholds as moment's fromNow(), which the dashboard used before
    function fromNow(time) {
        if (typeof Intl.RelativeTimeFormat !== 'function') {
            return format(time, 'fullDateTime');
        }

        if (!relativeFormatter) {
            relativeFormatter = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
        }

        var delta = time - Date.now();
        var abs = Math.abs(delta);
        var sign = delta < 0 ? -1 : 1;

        if (abs < 45 * SECOND) return relativeFormatter.format(0, 'second');
        if (abs < 45 * MINUTE) return relativeFormatter.format(sign * Math.max(1, Math.round(abs / MINUTE)), 'minute');
        if (abs < 22 * HOUR) return relativeFormatter.format(sign * Math.max(1, Math.round(abs / HOUR)), 'hour');
        if (abs < 26 * DAY) return relativeFormatter.format(sign * Math.max(1, Math.round(abs / DAY)), 'day');

        var months = Math.round(abs / (30.436875 * DAY));
        if (months < 11) return relativeFormatter.format(sign * Math.max(1, months), 'month');

        return relativeFormatter.format(sign * Math.max(1, Math.round(abs / (365.2425 * DAY))), 'year');
    }

    // Calendar arithmetic in local time, matching moment's semantics

    // Dates are always set in a single call: an intermediate date could fall into a daylight saving gap
    // and shift the time of day by an hour

    function mod(dividend, divisor) {
        return ((dividend % divisor) + divisor) % divisor;
    }

    function localDate(year, month, day) {
        return new Date(year, month, day).getTime();
    }

    function addMonths(time, amount) {
        var date = new Date(time);
        var total = date.getFullYear() * 12 + date.getMonth() + amount;
        var year = Math.floor(total / 12);
        var month = total - year * 12;

        // Clamp to the last day of the target month, e.g. Jan 31 + 1 month = Feb 28
        var lastDay = new Date(year, month + 1, 0).getDate();
        date.setFullYear(year, month, Math.min(date.getDate(), lastDay));

        return date.getTime();
    }

    function add(time, amount, unit) {
        var date;

        // Setting the same date again would re-resolve an ambiguous local time (when clocks fall back)
        if (amount === 0) return time;

        switch (unit) {
            case 'millisecond': return time + amount;
            case 'second': return time + amount * SECOND;
            case 'minute': return time + amount * MINUTE;
            case 'hour': return time + amount * HOUR;
            case 'day':
                date = new Date(time);
                date.setDate(date.getDate() + amount);
                return date.getTime();
            case 'week':
                date = new Date(time);
                date.setDate(date.getDate() + amount * 7);
                return date.getTime();
            case 'month': return addMonths(time, amount);
            case 'quarter': return addMonths(time, amount * 3);
            case 'year': return addMonths(time, amount * 12);
            default: return time;
        }
    }

    // A port of moment's monthDiff: fractional months, with end-of-month handling that starts
    // from the date with the larger day of month. Callers truncate the result.
    function monthDiff(a, b) {
        var dateA = new Date(a);
        var dateB = new Date(b);

        if (dateA.getDate() < dateB.getDate()) {
            return -monthDiff(b, a);
        }

        var wholeMonthDiff = (dateB.getFullYear() - dateA.getFullYear()) * 12 + (dateB.getMonth() - dateA.getMonth());
        var anchor = addMonths(a, wholeMonthDiff);
        var anchor2;
        var adjust;

        if (b - anchor < 0) {
            anchor2 = addMonths(a, wholeMonthDiff - 1);
            adjust = (b - anchor) / (anchor - anchor2);
        } else {
            anchor2 = addMonths(a, wholeMonthDiff + 1);
            adjust = (b - anchor) / (anchor2 - anchor);
        }

        return -(wholeMonthDiff + adjust) || 0;
    }

    function diff(max, min, unit) {
        // Day-based units ignore daylight saving shifts, like moment does
        var zoneDelta = (new Date(max).getTimezoneOffset() - new Date(min).getTimezoneOffset()) * MINUTE;

        switch (unit) {
            case 'millisecond': return Math.trunc(max - min);
            case 'second': return Math.trunc((max - min) / SECOND);
            case 'minute': return Math.trunc((max - min) / MINUTE);
            case 'hour': return Math.trunc((max - min) / HOUR);
            case 'day': return Math.trunc((max - min - zoneDelta) / DAY);
            case 'week': return Math.trunc((max - min - zoneDelta) / WEEK);
            case 'month': return Math.trunc(monthDiff(max, min)) || 0;
            case 'quarter': return Math.trunc(monthDiff(max, min) / 3) || 0;
            case 'year': return Math.trunc(monthDiff(max, min) / 12) || 0;
            default: return 0;
        }
    }

    function utcOffset(time) {
        return -new Date(time).getTimezoneOffset() * MINUTE;
    }

    function startOf(time, unit, weekday) {
        var date = new Date(time);
        var y = date.getFullYear(), m = date.getMonth(), d = date.getDate();

        switch (unit) {
            case 'second': return time - mod(time, SECOND);
            case 'minute': return time - mod(time, MINUTE);
            case 'hour': return time - mod(time + utcOffset(time), HOUR);
            case 'day': return localDate(y, m, d);
            // Sunday, as in moment's default locale. The dashboard's charts never use week units.
            case 'week': return localDate(y, m, d - date.getDay());
            case 'isoWeek':
                // Same as moment(time).isoWeekday(weekday) in Chart.js's moment adapter: moves to the given
                // weekday (1 = Monday ... 7 = Sunday) within the current ISO week, keeping the time of day
                return add(time, weekday - (date.getDay() || 7), 'day');
            case 'month': return localDate(y, m, 1);
            case 'quarter': return localDate(y, m - m % 3, 1);
            case 'year': return localDate(y, 0, 1);
            default: return time;
        }
    }

    function endOf(time, unit) {
        var date = new Date(time);
        var y = date.getFullYear(), m = date.getMonth(), d = date.getDate();

        switch (unit) {
            case 'second': return time + SECOND - mod(time, SECOND) - 1;
            case 'minute': return time + MINUTE - mod(time, MINUTE) - 1;
            case 'hour': return time + HOUR - mod(time + utcOffset(time), HOUR) - 1;
            case 'day': return localDate(y, m, d + 1) - 1;
            case 'week': return localDate(y, m, d - date.getDay() + 7) - 1;
            case 'month': return localDate(y, m + 1, 1) - 1;
            case 'quarter': return localDate(y, m - m % 3 + 3, 1) - 1;
            case 'year': return localDate(y + 1, 0, 1) - 1;
            default: return time;
        }
    }

    function parse(value) {
        if (value === null || value === undefined) return null;
        if (typeof value === 'number') return isFinite(value) ? value : null;
        if (value instanceof Date) return isNaN(value.getTime()) ? null : value.getTime();

        var parsed = Date.parse(value);
        return isNaN(parsed) ? null : parsed;
    }

    var HangfireIntl = {
        setLocale: setLocale,
        format: format,
        fromNow: fromNow,
        add: add,
        diff: diff,
        startOf: startOf,
        endOf: endOf,
        parse: parse
    };

    global.HangfireIntl = HangfireIntl;

    if (global.Chart && global.Chart._adapters && global.Chart._adapters._date) {
        global.Chart._adapters._date.override({
            formats: function () {
                return {
                    datetime: 'dateTimeWithSeconds',
                    millisecond: 'timeWithSeconds',
                    second: 'timeWithSeconds',
                    minute: 'time',
                    hour: 'time',
                    day: 'shortDate',
                    week: 'shortDate',
                    month: 'month',
                    quarter: 'month',
                    year: 'year'
                };
            },
            parse: function (value) { return parse(value); },
            format: function (time, name) { return format(time, name); },
            add: function (time, amount, unit) { return add(time, amount, unit); },
            diff: function (max, min, unit) { return diff(max, min, unit); },
            startOf: function (time, unit, weekday) { return startOf(time, unit, weekday); },
            endOf: function (time, unit) { return endOf(time, unit); }
        });
    }
})(typeof window !== 'undefined' ? window : this);
