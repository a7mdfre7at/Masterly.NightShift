(function (hangfire) {
    var changeDatasetColorScheme = function(newColorScheme) {
        this._chart.data.datasets[0].backgroundColor = COLORS[newColorScheme].failed.backgroundColor;
        this._chart.data.datasets[0].borderColor = COLORS[newColorScheme].failed.borderColor;

        this._chart.data.datasets[1].backgroundColor = COLORS[newColorScheme].deleted.backgroundColor;
        this._chart.data.datasets[1].borderColor = COLORS[newColorScheme].deleted.borderColor;

        this._chart.data.datasets[2].backgroundColor = COLORS[newColorScheme].succeeded.backgroundColor;
        this._chart.data.datasets[2].borderColor = COLORS[newColorScheme].succeeded.borderColor;
        
        this._chart.options.scales.xAxes[0].gridLines.color = COLORS[newColorScheme].cartesianColor;
        this._chart.options.scales.yAxes[0].gridLines.color = COLORS[newColorScheme].cartesianColor;

        this._chart.update();
    }

    var COLORS = {
        light: {
            cartesianColor: '#e5e5e5',
            failed: {
                backgroundColor: '#D55251',
                borderColor: null,
            },
            deleted: {
                backgroundColor: '#919191',
                borderColor: null,
            },
            succeeded: {
                backgroundColor: '#6FCD6D',
                borderColor: '#62B35F',
            },
        },
        dark: {
            cartesianColor: '#5f5f5f',
            failed: {
                backgroundColor: 'rgba(215, 58, 74, 0.4)',
            },
            deleted: {
                backgroundColor: 'rgba(204, 204, 204, 0.4)',
            },
            succeeded: {
                backgroundColor: 'rgba(87, 171, 90, 0.4)',
                borderColor: 'rgba(87, 171, 90, 1)',
            },
        },
    };

    hangfire.config = {
        pollInterval: $("#hangfireConfig").data("pollinterval"),
        pollUrl: $("#hangfireConfig").data("pollurl"),
        text: {
            refresh: $("#hangfireConfig").data("text-refresh") || "Refresh",
            live: $("#hangfireConfig").data("text-live") || "Live",
            liveHint: $("#hangfireConfig").data("text-live-hint") || "Refresh this list automatically"
        },
        locale: document.documentElement.lang,
        darkMode: $("#hangfireConfig").data("darkmode")
    };

    // The effective color scheme: the chosen theme, or the system's when none is chosen
    var effectiveScheme = function () {
        if (!hangfire.config.darkMode) return "light";
        var chosen = document.documentElement.getAttribute('data-theme');
        if (chosen === 'dark' || chosen === 'light') return chosen;
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? "dark" : "light";
    };

    var colorScheme = effectiveScheme();

    var applySchemeToCharts = function () {
        colorScheme = effectiveScheme();
        if (hangfire.page && hangfire.page.realtimeGraph) hangfire.page.realtimeGraph.changeDatasetColorScheme(colorScheme);
        if (hangfire.page && hangfire.page.historyGraph) hangfire.page.historyGraph.changeDatasetColorScheme(colorScheme);
    };

    if (hangfire.config.darkMode && window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', applySchemeToCharts);
    }

    $(document).on('hangfire:theme', applySchemeToCharts);

    hangfire.Theme = (function () {
        var STORAGE_KEY = 'hangfire.theme';

        function Theme() {
            this._buttons = $('.js-theme button[data-theme-choice]');
            this._stylesheet = document.getElementById('hfDarkStylesheet');

            var self = this;
            this._buttons.on('click', function () {
                self.set($(this).data('theme-choice'));
            });

            this.apply(this.get(), false);
        }

        Theme.prototype.get = function () {
            try {
                var value = window.localStorage.getItem(STORAGE_KEY);
                return value === 'light' || value === 'dark' ? value : 'system';
            } catch (e) {
                return 'system';
            }
        };

        Theme.prototype.set = function (choice) {
            try {
                if (choice === 'system') window.localStorage.removeItem(STORAGE_KEY);
                else window.localStorage.setItem(STORAGE_KEY, choice);
            } catch (e) { /* private mode */ }

            this.apply(choice, true);
        };

        Theme.prototype.apply = function (choice, notify) {
            var root = document.documentElement;

            if (choice === 'light' || choice === 'dark') {
                root.setAttribute('data-theme', choice);
            } else {
                root.removeAttribute('data-theme');
            }

            if (this._stylesheet) {
                this._stylesheet.media = choice === 'dark' ? 'all' : choice === 'light' ? 'not all' : '(prefers-color-scheme: dark)';
            }

            this._buttons.removeClass('active').filter('[data-theme-choice="' + choice + '"]').addClass('active');

            if (notify) {
                $(document).trigger('hangfire:theme');
            }
        };

        return Theme;
    })();

    hangfire.Shell = (function () {
        function Shell() {
            var body = $('body');

            $(document).on('click', '.js-sidebar-toggle', function (e) {
                e.stopPropagation();
                body.toggleClass('hf-sidebar-open');
            });

            $(document).on('click', function (e) {
                if (body.hasClass('hf-sidebar-open') && !$(e.target).closest('#hfSidebar').length) {
                    body.removeClass('hf-sidebar-open');
                }
            });

            $(document).on('keydown', function (e) {
                if (e.key === 'Escape') body.removeClass('hf-sidebar-open');
            });

            $(document).on('click', '.js-page-refresh', function () {
                $(this).addClass('spinning');
                window.location.reload();
            });
        }

        Shell.setOnline = function (online) {
            $('.js-pulse-dot').toggleClass('offline', !online);
        };

        return Shell;
    })();

    hangfire.ErrorAlert = (function () {
        function ErrorAlert(title, message) {
            this._errorAlert = $('#errorAlert');
            this._errorAlertTitle = $('#errorAlertTitle');
            this._errorAlertMessage = $('#errorAlertMessage');
            this._title = title;
            this._message = message;
        }

        ErrorAlert.prototype.show = function() {
            this._errorAlertTitle.html(this._title);
            this._errorAlertMessage.html(this._message);

            $('#errorAlert').show();
            var alertHeight = $('#errorAlert').outerHeight();
            $('#errorAlert').hide();

            $('#errorAlert').slideDown("fast");
            $('.js-page-container').animate({ 'padding-top': alertHeight + 'px' }, "fast");
        };

        return ErrorAlert;
    })();

    hangfire.Metrics = (function() {
        function Metrics() {
            this._metrics = {};
        }

        Metrics.prototype.addElement = function(name, element) {
            if (!(name in this._metrics)) {
                this._metrics[name] = [];
            }

            this._metrics[name].push(element);
        };

        Metrics.prototype.getElements = function(name) {
            if (!(name in this._metrics)) {
                return [];
            }

            return this._metrics[name];
        };

        Metrics.prototype.getNames = function() {
            var result = [];
            var metrics = this._metrics;

            for (var name in metrics) {
                if (metrics.hasOwnProperty(name)) {
                    result.push(name);
                }
            }

            return result;
        };

        return Metrics;
    })();

    hangfire.RealtimeGraph = (function() {
        function RealtimeGraph(element, succeeded, failed, deleted, succeededStr, failedStr, deletedStr, pollInterval) {
            this._succeeded = succeeded;
            this._failed = failed;
            this._deleted = deleted;
            this._last = Date.now();
            this._pollInterval = pollInterval;
            
            this._chart = new Chart(element, {
                type: 'line',
                data: {
                    datasets: [
                        { 
                            label: failedStr,  
                            borderColor: COLORS[colorScheme].failed.borderColor, 
                            backgroundColor: COLORS[colorScheme].failed.backgroundColor, 
                            borderWidth: 2 
                        },
                        { 
                            label: deletedStr,
                            borderColor: COLORS[colorScheme].deleted.borderColor, 
                            backgroundColor: COLORS[colorScheme].deleted.backgroundColor, 
                            borderWidth: 2 
                        },
                        { 
                            label: succeededStr,
                            borderColor: COLORS[colorScheme].succeeded.borderColor, 
                            backgroundColor: COLORS[colorScheme].succeeded.backgroundColor 
                        },
                    ]
                },
                options: {
                    scales: {
                        xAxes: [{
                            gridLines: { color: COLORS[colorScheme].cartesianColor },
                            type: 'realtime',
                            realtime: { duration: 60 * 1000, delay: pollInterval },
                            time: { unit: 'second', tooltipFormat: 'dateTimeWithSeconds', displayFormats: { second: 'timeWithSeconds', minute: 'timeWithSeconds' } },
                            ticks: { maxRotation: 0 }
                        }],
                        yAxes: [{
                            gridLines: { color: COLORS[colorScheme].cartesianColor },
                            ticks: { beginAtZero: true, precision: 0, min: 0, maxTicksLimit: 6, suggestedMax: 10 }, 
                            stacked: true 
                        }]
                    },
                    elements: { line: { tension: 0 }, point: { radius: 0 } },
                    animation: { duration: 0 },
                    hover: { animationDuration: 0 },
                    responsiveAnimationDuration: 0,
                    legend: { display: false },
                    tooltips: { mode: 'index', intersect: false }
                }
            });
        }

        RealtimeGraph.prototype.appendHistory = function (statistics) {
            var newFailed = parseInt(statistics["failed:count"].intValue);
            var newDeleted = parseInt(statistics["deleted:count"].intValue);
            var newSucceeded = parseInt(statistics["succeeded:count"].intValue);
            var now = Date.now();

            if (this._succeeded !== null && this._failed !== null && this._deleted !== null && (now - this._last < this._pollInterval * 2)) {
                var failed = Math.max(newFailed - this._failed, 0);
                var deleted = Math.max(newDeleted - this._deleted, 0);
                var succeeded = Math.max(newSucceeded - this._succeeded, 0);

                this._chart.data.datasets[0].data.push({ x: now, y: failed });
                this._chart.data.datasets[1].data.push({ x: now, y: deleted });
                this._chart.data.datasets[2].data.push({ x: now, y: succeeded });
                
                this._chart.update();
            }
            
            this._failed = newFailed;
            this._deleted = newDeleted;
            this._succeeded = newSucceeded;
            this._last = now;
        };

        RealtimeGraph.prototype.changeDatasetColorScheme = changeDatasetColorScheme;

        return RealtimeGraph;
    })();

    hangfire.HistoryGraph = (function() {
        function HistoryGraph(element, succeeded, failed, deleted, succeededStr, failedStr, deletedStr) {
            var timeOptions = $(element).data('period') === 'week'
                ? { unit: 'day', tooltipFormat: 'longDate', displayFormats: { day: 'shortDate' } }
                : { unit: 'hour', tooltipFormat: 'dateTime', displayFormats: { hour: 'time', day: 'shortDate' } };

            this._chart = new Chart(element, {
                type: 'line',
                data: {
                    datasets: [
                        { 
                            label: failedStr,  
                            borderColor: COLORS[colorScheme].failed.borderColor, 
                            backgroundColor: COLORS[colorScheme].failed.backgroundColor, 
                            borderWidth: 2,
                            data: failed,
                        },
                        { 
                            label: deletedStr,
                            borderColor: COLORS[colorScheme].deleted.borderColor, 
                            backgroundColor: COLORS[colorScheme].deleted.backgroundColor, 
                            borderWidth: 2,
                            data: deleted,
                        },
                        { 
                            label: succeededStr,
                            borderColor: COLORS[colorScheme].succeeded.borderColor, 
                            backgroundColor: COLORS[colorScheme].succeeded.backgroundColor,
                            data: succeeded,
                        },
                    ]
                },
                options: {
                    scales: {
                        xAxes: [{ gridLines: { color: COLORS[colorScheme].cartesianColor }, type: 'time', time: timeOptions, ticks: { maxRotation: 0 } }],
                        yAxes: [{ gridLines: { color: COLORS[colorScheme].cartesianColor }, ticks: { beginAtZero: true, precision: 0, maxTicksLimit: 6 }, stacked: true }]
                    },
                    elements: { line: { tension: 0 }, point: { radius: 0 } },
                    legend: { display: false },
                    tooltips: { mode: 'index', intersect: false },
                    plugins: { streaming: false },
                }
            });

            HistoryGraph.prototype.changeDatasetColorScheme = changeDatasetColorScheme;

        }

        return HistoryGraph;
    })();

    hangfire.StatisticsPoller = (function() {
        function StatisticsPoller(metricsCallback, statisticsUrl, pollInterval) {
            this._metricsCallback = metricsCallback;
            this._listeners = [];
            this._statisticsUrl = statisticsUrl;
            this._pollInterval = pollInterval;
            this._timeoutId = null;
        }

        StatisticsPoller.prototype.start = function () {
            var self = this;

            var intervalFunc = function() {
                try {
                    $.post(self._statisticsUrl, { metrics: self._metricsCallback() })
                        .done(function (data) {
                            Hangfire.Shell.setOnline(true);
                            self._notifyListeners(data);
                            if (self._timeoutId !== null) {
                                self._timeoutId = setTimeout(intervalFunc, self._pollInterval);
                            }
                        })
                        .fail(function (xhr) {
                            Hangfire.Shell.setOnline(false);
                            var errorAlert = new Hangfire.ErrorAlert(
                                'Unable to refresh the statistics:',
                                'the server responded with ' + xhr.status + ' (' + xhr.statusText
                                + '). Try reloading the page manually, or wait for automatic reload that will happen in a minute.');

                            errorAlert.show();
                            self._timeoutId = null;
                            setTimeout(function() { window.location.reload(); }, 60*1000);
                        });
                } catch (e) {
                    console.log(e);
                }
            };

            this._timeoutId = setTimeout(intervalFunc, this._pollInterval);
        };

        StatisticsPoller.prototype.stop = function() {
            if (this._timeoutId !== null) {
                clearTimeout(this._timeoutId);
                this._timeoutId = null;
            }
        };

        StatisticsPoller.prototype.addListener = function(listener) {
            this._listeners.push(listener);
        };

        StatisticsPoller.prototype._notifyListeners = function(statistics) {
            var length = this._listeners.length;
            var i;
            
            for (i = 0; i < length; i++) {
                this._listeners[i](statistics);
            }
        };

        return StatisticsPoller;
    })();

    hangfire.RecurringJobEditor = (function () {
        function RecurringJobEditor(modal) {
            this._modal = $(modal);
            this._form = this._modal.find('.js-recurring-edit-form');
            this._cron = this._modal.find('.js-recurring-edit-cron');
            this._timezone = this._modal.find('.js-recurring-edit-timezone');
            this._queue = this._modal.find('.js-recurring-edit-queue');
            this._misfire = this._modal.find('.js-recurring-edit-misfire');
            this._preview = this._modal.find('.js-recurring-edit-preview');
            this._description = this._modal.find('.js-recurring-edit-description');
            this._resolved = this._modal.find('.js-recurring-edit-resolved');
            this._error = this._modal.find('.js-recurring-edit-error');
            this._save = this._modal.find('.js-recurring-edit-save');
            this._previewUrl = this._modal.data('preview-url');
            this._updateUrl = this._modal.data('update-url');
            this._previewUnavailable = this._modal.data('preview-unavailable');
            this._jobId = null;
            this._previewTimer = null;
            this._previewRequest = null;

            var self = this;

            $(document).on('click', '.js-recurring-edit', function (e) {
                e.preventDefault();
                e.stopPropagation();
                self.open($(this));
            });

            this._cron.add(this._timezone).on('input change', function () {
                self._schedulePreview();
            });

            this._form.on('submit', function (e) {
                e.preventDefault();
                self._submit();
            });

            // Deep link: /recurring?edit=<id> opens the editor for that job (used by the command palette)
            var match = /[?&]edit=([^&]+)/.exec(window.location.search);
            if (match) {
                var button = $('.js-recurring-edit').filter(function () {
                    return String($(this).data('id')) === decodeURIComponent(match[1].replace(/\+/g, ' '));
                }).first();

                if (button.length) {
                    this.open(button);
                }
            }
        }

        RecurringJobEditor.prototype.open = function (button) {
            this._jobId = String(button.data('id'));
            this._modal.find('.js-recurring-edit-id').text(this._jobId);
            this._cron.val(button.data('cron') || '');
            this._timezone.val(button.data('timezone') || 'UTC');
            this._queue.val(button.data('queue') || '');
            this._misfire.val(button.data('misfire') || 'Relaxed');
            this._error.hide().text('');
            this._preview.empty();
            this._description.text('');
            this._resolved.text('');
            this._modal.modal('show');
            this._refreshPreview();

            var cron = this._cron;
            this._modal.one('shown.bs.modal', function () { cron.trigger('focus'); });
        };

        RecurringJobEditor.prototype._schedulePreview = function () {
            var self = this;
            clearTimeout(this._previewTimer);
            this._previewTimer = setTimeout(function () { self._refreshPreview(); }, 300);
        };

        RecurringJobEditor.prototype._refreshPreview = function () {
            var self = this;
            var cron = $.trim(this._cron.val());

            if (this._previewRequest) {
                this._previewRequest.abort();
            }

            if (!cron) {
                this._preview.empty();
                this._description.text('');
                this._resolved.text('');
                return;
            }

            this._previewRequest = $.get(this._previewUrl, { cron: cron, timezone: this._timezone.val() })
                .done(function (data) {
                    self._preview.empty();

                    if (data.error) {
                        self._resolved.text('');
                        self._description.text('');
                        self._showError(data.error);
                        return;
                    }

                    self._error.hide();
                    self._resolved.text(data.cron);
                    self._description.text(data.description || '');

                    if (!data.next || data.next.length === 0) {
                        self._preview.append($('<li>').addClass('text-muted').text(self._previewUnavailable));
                        return;
                    }

                    var format = data.hasSeconds ? 'dateTimeWithSeconds' : 'fullDateTime';

                    for (var i = 0; i < data.next.length; i++) {
                        var time = data.next[i] * 1000;
                        self._preview.append($('<li>')
                            .append($('<span>').text(HangfireIntl.format(time, format)))
                            .append($('<span>').addClass('text-muted').text(HangfireIntl.fromNow(time))));
                    }
                })
                .fail(function (xhr, status) {
                    if (status !== 'abort') {
                        self._showError(self._previewUnavailable);
                    }
                });
        };

        RecurringJobEditor.prototype._submit = function () {
            var self = this;
            var save = this._save;

            save.button('loading');
            this._error.hide();

            $.post(this._updateUrl + encodeURIComponent(this._jobId), {
                cron: $.trim(this._cron.val()),
                timezone: this._timezone.val(),
                queue: $.trim(this._queue.val()),
                misfire: this._misfire.val()
            })
                .done(function () { window.location.reload(); })
                .fail(function (xhr) {
                    save.button('reset');
                    var message = xhr.responseJSON && xhr.responseJSON.error
                        ? xhr.responseJSON.error
                        : 'The server responded with ' + xhr.status + ' (' + xhr.statusText + ').';
                    self._showError(message);
                });
        };

        RecurringJobEditor.prototype._showError = function (message) {
            this._error.text(message).show();
        };

        return RecurringJobEditor;
    })();

    hangfire.LiveJobs = (function () {
        function LiveJobs(pollInterval) {
            this._pollInterval = Math.max(pollInterval || 2000, 1000);
            this._details = $('#jobLive');
            this._rows = $('.js-live-row[data-job-id]');
            this._url = this._details.data('live-url') || $('#hangfireConfig').data('liveurl');
            this._timer = null;

            if (!this._url) return;

            if (this._details.length && this._details.data('poll') === true) {
                this._startDetails();
            } else if (this._rows.length) {
                this._startRows();
            }
        }

        LiveJobs.prototype._startDetails = function () {
            var self = this;
            var jobId = String(this._details.data('job-id'));
            var initialState = this._details.data('state');
            var startedAt = parseInt(this._details.data('started-at'));

            this._tickElapsed(startedAt);

            var poll = function () {
                $.post(self._url, { 'jobs[]': [jobId] })
                    .done(function (data) {
                        var job = data.jobs && data.jobs[jobId];

                        if (!job || job.state !== initialState) {
                            // Finished, failed or moved on: show the final page
                            window.location.reload();
                            return;
                        }

                        self._renderDetails(job, data.consoleSupported);
                        self._timer = setTimeout(poll, self._pollInterval);
                    })
                    .fail(function () {
                        self._timer = setTimeout(poll, self._pollInterval * 3);
                    });
            };

            this._timer = setTimeout(poll, this._pollInterval);
        };

        LiveJobs.prototype._tickElapsed = function (startedAt) {
            if (!startedAt) return;

            var self = this;
            var element = this._details.find('.js-live-elapsed');
            var update = function () {
                element.text(LiveJobs.formatDuration(Date.now() - startedAt * 1000));
            };

            update();
            setInterval(update, 1000);
            this._details.find('.js-live-elapsed-label').show();
        };

        LiveJobs.prototype._renderDetails = function (job, consoleSupported) {
            var bar = this._details.find('.js-live-bar');
            var percent = job.progress ? job.progress.p : null;

            if (percent !== null && percent !== undefined) {
                bar.css('width', percent + '%').attr('aria-valuenow', percent).text(percent + '%');
                this._details.find('.js-live-progress-message').text(job.progress.m || '');
            }

            if (!consoleSupported) return;

            var console = this._details.find('.js-live-console');
            var atBottom = console.scrollTop() + console.innerHeight() >= console[0].scrollHeight - 10;
            var lines = [];
            var checkpoints = [];
            var previous = null;

            for (var i = 0; i < job.entries.length; i++) {
                var entry = job.entries[i];
                var time = $('<span>').addClass('job-console-time').text(HangfireIntl.format(entry.t, 'timeWithSeconds'));

                if (entry.c) {
                    lines.push($('<div>').addClass('job-console-line job-console-checkpoint').append(time)
                        .append($('<span>').addClass('glyphicon glyphicon-flag')).append(document.createTextNode(' ' + entry.c)));

                    var delta = previous !== null ? '+' + LiveJobs.formatDuration(entry.t - previous) : HangfireIntl.format(entry.t, 'timeWithSeconds');
                    checkpoints.push($('<li>').append($('<span>').addClass('job-checkpoint-name').text(entry.c))
                        .append($('<span>').addClass('text-muted job-checkpoint-delta').text(delta)));
                    previous = entry.t;
                } else {
                    lines.push($('<div>').addClass('job-console-line job-console-' + (entry.l || 'info')).append(time).append(document.createTextNode(entry.m || '')));
                }
            }

            if (lines.length > 0) {
                console.empty().append(lines);
                if (atBottom) {
                    console.scrollTop(console[0].scrollHeight);
                }
            }

            this._details.find('.js-live-checkpoints').empty().append(checkpoints);
        };

        LiveJobs.prototype._startRows = function () {
            var self = this;
            var ids = this._rows.map(function () { return String($(this).data('job-id')); }).get();

            var poll = function () {
                $.post(self._url, { 'jobs[]': ids })
                    .done(function (data) {
                        self._rows.each(function () {
                            var row = $(this);
                            var job = data.jobs && data.jobs[String(row.data('job-id'))];
                            if (!job) return;

                            if (job.progress) {
                                row.find('.js-live-bar').css('width', job.progress.p + '%');
                                row.find('.js-live-progress-message').text(job.progress.p + '% ' + (job.progress.m || ''));
                                row.find('.progress').attr('title', job.progress.m || '');
                            }

                            if (job.state !== 'Processing') {
                                row.closest('tr').addClass('obsolete-data');
                            }
                        });

                        self._timer = setTimeout(poll, self._pollInterval);
                    })
                    .fail(function () {
                        self._timer = setTimeout(poll, self._pollInterval * 3);
                    });
            };

            this._timer = setTimeout(poll, this._pollInterval);
        };

        LiveJobs.formatDuration = function (milliseconds) {
            if (milliseconds < 0) milliseconds = 0;

            var totalSeconds = Math.floor(milliseconds / 1000);
            var hours = Math.floor(totalSeconds / 3600);
            var minutes = Math.floor((totalSeconds % 3600) / 60);
            var seconds = totalSeconds % 60;

            if (hours > 0) return hours + 'h ' + minutes + 'm ' + seconds + 's';
            if (minutes > 0) return minutes + 'm ' + seconds + 's';
            if (totalSeconds > 0) return seconds + 's';
            return milliseconds + 'ms';
        };

        return LiveJobs;
    })();

    hangfire.Page = (function() {
        function Page(config) {
            this._metrics = new Hangfire.Metrics();

            var self = this;
            this._poller = new Hangfire.StatisticsPoller(
                function () { return self._metrics.getNames(); },
                config.pollUrl,
                config.pollInterval);

            this._initialize(config.locale);

            this.realtimeGraph = this._createRealtimeGraph('realtimeGraph', config.pollInterval);
            this.historyGraph = this._createHistoryGraph('historyGraph');

            var editorElement = document.getElementById('recurringJobEditor');
            this.recurringJobEditor = editorElement ? new Hangfire.RecurringJobEditor(editorElement) : null;
            this.theme = new Hangfire.Theme();
            this.shell = new Hangfire.Shell();
            this.liveJobs = new Hangfire.LiveJobs(config.pollInterval);
            this.commandPalette = new Hangfire.CommandPalette(config);
            this.liveTable = new Hangfire.LiveTable(this, config);

            this._poller.start();
        };

        Page.prototype._createRealtimeGraph = function(elementId, pollInterval) {
            var realtimeElement = document.getElementById(elementId);
            if (realtimeElement) {
                var succeeded = parseInt($(realtimeElement).data('succeeded'));
                var failed = parseInt($(realtimeElement).data('failed'));
                var deleted = parseInt($(realtimeElement).data('deleted'));

                var succeededStr = $(realtimeElement).data('succeeded-string');
                var failedStr = $(realtimeElement).data('failed-string');
                var deletedStr = $(realtimeElement).data('deleted-string');
                var realtimeGraph = new Hangfire.RealtimeGraph(realtimeElement, succeeded, failed, deleted, succeededStr, failedStr, deletedStr, pollInterval);

                this._poller.addListener(function (data) {
                    realtimeGraph.appendHistory(data);
                });

                return realtimeGraph;
            }

            return null;
        };

        Page.prototype._createHistoryGraph = function(elementId) {
            var historyElement = document.getElementById(elementId);
            if (historyElement) {
                var createSeries = function (obj) {
                    var series = [];
                    for (var date in obj) {
                        if (obj.hasOwnProperty(date)) {
                            var value = obj[date];
                            var point = { x: Date.parse(date), y: value };
                            series.unshift(point);
                        }
                    }
                    return series;
                };

                var succeeded = createSeries($(historyElement).data("succeeded"));
                var failed = createSeries($(historyElement).data("failed"));
                var deleted = createSeries($(historyElement).data("deleted"));

                var succeededStr = $(historyElement).data('succeeded-string');
                var failedStr = $(historyElement).data('failed-string');
                var deletedStr = $(historyElement).data('deleted-string');

                return new Hangfire.HistoryGraph(historyElement, succeeded, failed, deleted, succeededStr, failedStr, deletedStr);
            }

            return null;
        };

        Page.prototype.updateRelativeDates = function () {
            this._updateRelativeDates();
        };

        Page.prototype._initialize = function (locale) {
            HangfireIntl.setLocale(locale);
            var self = this;
            var updateRelativeDates = function () {
                $('*[data-moment]').each(function () {
                    var $this = $(this);
                    var timestamp = $this.data('moment');

                    if (timestamp) {
                        var time = timestamp * 1000;
                        $this.html(HangfireIntl.fromNow(time))
                            .attr('title', HangfireIntl.format(time, 'fullDateTime'))
                            .attr('data-container', 'body');
                    }
                });

                $('*[data-moment-title]').each(function () {
                    var $this = $(this);
                    var timestamp = $this.data('moment-title');

                    if (timestamp) {
                        var time = timestamp * 1000;
                        $this.prop('title', HangfireIntl.format(time, 'fullDateTime'))
                            .attr('data-container', 'body');
                    }
                });

                $('*[data-moment-local]').each(function () {
                    var $this = $(this);
                    var timestamp = $this.data('moment-local');

                    if (timestamp) {
                        var time = timestamp * 1000;
                        $this.html(HangfireIntl.format(time, 'numericDateTime'));
                    }
                });
            };

            this._updateRelativeDates = updateRelativeDates;
            updateRelativeDates();
            setInterval(updateRelativeDates, 30 * 1000);

            $('*[title]').tooltip();

            $('*[data-metric]').each(function () {
                var name = $(this).data('metric');
                self._metrics.addElement(name, this);
            });

            this._poller.addListener(function (metrics) {
                for (var name in metrics) {
                    var elements = self._metrics.getElements(name);
                    for (var i = 0; i < elements.length; i++) {
                        var metric = metrics[name];
                        var metricClass = metric ? "metric-" + metric.style : "metric-null";
                        var highlighted = metric && metric.highlighted ? "highlighted" : null;
                        var value = metric ? metric.value : null;

                        $(elements[i])
                            .text(value)
                            .closest('.metric')
                            .removeClass()
                            .addClass(["metric", metricClass, highlighted].join(' '));
                    }
                }
            });

            var csrfHeader = $('meta[name="csrf-header"]').attr('content');
            var csrfToken = $('meta[name="csrf-token"]').attr('content');

            if (csrfToken && csrfHeader) {
                var headers = {};
                headers[csrfHeader] = csrfToken;

                $.ajaxSetup({ headers: headers });
            }

            $(document).on('click', '*[data-ajax]', function (e) {
                var $this = $(this);
                var confirmText = $this.data('confirm');

                if (!confirmText || confirm(confirmText)) {
                    $this.prop('disabled');
                    var loadingDelay = setTimeout(function() {
                        $this.button('loading');
                    }, 100);

                    $.post($this.data('ajax'), function() {
                        clearTimeout(loadingDelay);
                        window.location.reload();
                    });
                }

                e.preventDefault();
            });

            $(document).on('click', '.expander', function (e) {
                var $expander = $(this),
                    $expandable = $expander.closest('tr').next().find('.expandable');

                $expander.trigger('blur');

                if (!$expandable.is(':visible')) {
                    $expander.text('Fewer details...');
                }

				$expandable.slideToggle(
					150, 
					function() {
					    if (!$expandable.is(':visible')) {
					        $expander.text('More details...');
					    }
					});
                e.preventDefault();
            });

            $('.js-jobs-list').each(function () {
                self.initJobsList(this);
            });
        };

        Page.prototype.initJobsList = function (container) {
            (function () {
                var selectRow = function(row, isSelected) {
                    var $checkbox = $('.js-jobs-list-checkbox', row);
                    if ($checkbox.length > 0) {
                        $checkbox.prop('checked', isSelected);
                        $(row).toggleClass('highlight', isSelected);
                    }
                };

                var toggleRowSelection = function(row) {
                    var $checkbox = $('.js-jobs-list-checkbox', row);
                    if ($checkbox.length > 0) {
                        var isSelected = $checkbox.is(':checked');
                        selectRow(row, !isSelected);
                    }
                };

                var setListState = function (state) {
                    $('.js-jobs-list-select-all', container)
                        .prop('checked', state === 'all-selected')
                        .prop('indeterminate', state === 'some-selected');
                    
                    $('.js-jobs-list-command', container)
                        .prop('disabled', state === 'none-selected');
                };

                var updateListState = function() {
                    var selectedRows = $('.js-jobs-list-checkbox', container).map(function() {
                        return $(this).prop('checked');
                    }).get();

                    var state = 'none-selected';

                    if (selectedRows.length > 0) {
                        state = 'some-selected';

                        if ($.inArray(false, selectedRows) === -1) {
                            state = 'all-selected';
                        } else if ($.inArray(true, selectedRows) === -1) {
                            state = 'none-selected';
                        }
                    }

                    setListState(state);
                };

                $(container).off('click.hangfire');

                $(container).on('click.hangfire', '.js-jobs-list-checkbox', function(e) {
                    selectRow(
                        $(this).closest('.js-jobs-list-row').first(),
                        $(this).is(':checked'));

                    updateListState();

                    e.stopPropagation();
                });

                $(container).on('click.hangfire', '.js-jobs-list-row', function (e) {
                    if ($(e.target).is('a') || $(e.target).closest('button').length) return;

                    toggleRowSelection(this);
                    updateListState();
                });

                $(container).on('click.hangfire', '.js-jobs-list-select-all', function() {
                    var selectRows = $(this).is(':checked');

                    $('.js-jobs-list-row', container).each(function() {
                        selectRow(this, selectRows);
                    });

                    updateListState();
                });

                $(container).on('click.hangfire', '.js-jobs-list-command', function(e) {
                    var $this = $(this);
                    var confirmText = $this.data('confirm');

                    var jobs = $("input[name='jobs[]']:checked", container).map(function() {
                        return $(this).val();
                    }).get();

                    if (!confirmText || confirm(confirmText)) {
                        $this.prop('disabled');
                        var loadingDelay = setTimeout(function () {
                            $this.button('loading');
                        }, 100);

                        $.post($this.data('url'), { 'jobs[]': jobs }, function () {
                            clearTimeout(loadingDelay);
                            window.location.reload();
                        });
                    }

                    e.preventDefault();
                });

                updateListState();
            })();
        };

        return Page;
    })();

    hangfire.CommandPalette = (function () {
        var DEFAULT_PAGES = [
            { name: 'Overview', path: '/' },
            { name: 'Enqueued jobs', path: '/jobs/enqueued' },
            { name: 'Scheduled jobs', path: '/jobs/scheduled' },
            { name: 'Processing jobs', path: '/jobs/processing' },
            { name: 'Succeeded jobs', path: '/jobs/succeeded' },
            { name: 'Failed jobs', path: '/jobs/failed' },
            { name: 'Deleted jobs', path: '/jobs/deleted' },
            { name: 'Awaiting jobs', path: '/jobs/awaiting' },
            { name: 'Retries', path: '/retries' },
            { name: 'Recurring jobs', path: '/recurring' },
            { name: 'Insights', path: '/insights' },
            { name: 'Servers', path: '/servers' }
        ];

        function CommandPalette(config) {
            this._basePath = (config.pollUrl || '').replace(/\/stats$/, '');
            this._searchUrl = this._basePath + '/search';
            this._pages = this._collectPages();
            this._results = [];
            this._selected = 0;
            this._request = null;
            this._timer = null;

            this._build();
            this._bindKeys();
        }

        CommandPalette.prototype._collectPages = function () {
            var self = this;
            var pages = DEFAULT_PAGES.map(function (page) { return { name: page.name, url: self._basePath + page.path }; });
            var seen = {};
            pages.forEach(function (page) { seen[page.url] = true; });

            // Pages added by extensions show up in the navigation
            $('.hf-nav a[href], .navbar-nav a[href], .list-group a[href]').each(function () {
                var url = $(this).attr('href');
                var name = $.trim($(this).clone().children().remove().end().text());
                if (url && name && !seen[url]) {
                    seen[url] = true;
                    pages.push({ name: name, url: url });
                }
            });

            return pages;
        };

        CommandPalette.prototype._build = function () {
            this._overlay = $('<div class="hf-palette" role="dialog" aria-modal="true" style="display: none">')
                .append($('<div class="hf-palette-box">')
                    .append(this._input = $('<input type="text" class="hf-palette-input" placeholder="Jump to a page, a job id or a recurring job…" autocomplete="off" spellcheck="false">'))
                    .append(this._list = $('<ul class="hf-palette-results">'))
                    .append($('<div class="hf-palette-hint">').html('<kbd>↑</kbd><kbd>↓</kbd> navigate &nbsp; <kbd>Enter</kbd> open &nbsp; <kbd>Esc</kbd> close &nbsp; <span class="hf-palette-hint-right"><kbd>Ctrl</kbd>+<kbd>K</kbd> or <kbd>/</kbd> opens this anywhere</span>')));

            $('body').append(this._overlay);

            var self = this;

            this._overlay.on('click', function (e) {
                if (e.target === self._overlay[0]) self.close();
            });

            this._input.on('input', function () {
                clearTimeout(self._timer);
                self._timer = setTimeout(function () { self._search(); }, 150);
            });

            this._input.on('keydown', function (e) {
                if (e.key === 'ArrowDown') { self._move(1); e.preventDefault(); }
                else if (e.key === 'ArrowUp') { self._move(-1); e.preventDefault(); }
                else if (e.key === 'Enter') { self._open(); e.preventDefault(); }
                else if (e.key === 'Escape') { self.close(); e.preventDefault(); }
            });

            this._list.on('click', 'li', function () {
                self._selected = $(this).index();
                self._open();
            });

            $(document).on('click', '.js-palette-open', function (e) {
                e.preventDefault();
                self.open();
            });
        };

        CommandPalette.prototype._bindKeys = function () {
            var self = this;
            var pendingG = null;

            $(document).on('keydown', function (e) {
                var inField = $(e.target).is('input, textarea, select, [contenteditable]');

                if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
                    e.preventDefault();
                    self.toggle();
                    return;
                }

                if (inField) return;

                if (e.key === '/') {
                    e.preventDefault();
                    self.open();
                    return;
                }

                if (e.key === '?') {
                    self.open('');
                    return;
                }

                // "g" then a letter: go to a page
                if (e.key === 'g') {
                    pendingG = Date.now();
                    return;
                }

                if (pendingG && Date.now() - pendingG < 1500) {
                    var targets = { j: '/jobs/enqueued', p: '/jobs/processing', f: '/jobs/failed', r: '/recurring', s: '/servers', i: '/insights', h: '/' };
                    if (targets[e.key]) {
                        window.location.href = self._basePath + targets[e.key];
                    }
                    pendingG = null;
                }
            });
        };

        CommandPalette.prototype.open = function (query) {
            this._overlay.show();
            if (query !== undefined) this._input.val(query);
            this._input.trigger('focus').trigger('select');
            this._search();
        };

        CommandPalette.prototype.close = function () {
            this._overlay.hide();
            if (this._request) this._request.abort();
        };

        CommandPalette.prototype.toggle = function () {
            if (this._overlay.is(':visible')) this.close(); else this.open();
        };

        CommandPalette.prototype._search = function () {
            var self = this;
            var query = $.trim(this._input.val());
            var lower = query.toLowerCase();

            var results = this._pages
                .filter(function (page) { return !lower || page.name.toLowerCase().indexOf(lower) >= 0; })
                .slice(0, lower ? 6 : 12)
                .map(function (page) { return { group: 'Pages', title: page.name, url: page.url }; });

            this._render(results);

            if (this._request) this._request.abort();
            if (!query) return;

            this._request = $.get(this._searchUrl, { q: query })
                .done(function (data) {
                    (data.jobs || []).forEach(function (job) {
                        results.push({ group: 'Jobs', title: '#' + job.id + (job.name ? ' — ' + job.name : ''), detail: job.state, url: job.url });
                    });
                    (data.recurringJobs || []).forEach(function (job) {
                        results.push({ group: 'Recurring jobs', title: job.id + (job.name ? ' — ' + job.name : ''), detail: (job.paused ? 'paused · ' : '') + (job.cron || ''), url: job.url });
                    });
                    self._render(results);
                });
        };

        CommandPalette.prototype._render = function (results) {
            this._results = results;
            this._selected = Math.min(this._selected, Math.max(0, results.length - 1));
            this._list.empty();

            var group = null;

            for (var i = 0; i < results.length; i++) {
                var result = results[i];

                if (result.group !== group) {
                    group = result.group;
                    this._list.append($('<li class="hf-palette-group">').text(group));
                }

                var item = $('<li class="hf-palette-item">').toggleClass('selected', i === this._selected)
                    .append($('<span class="hf-palette-title">').text(result.title));

                if (result.detail) {
                    item.append($('<span class="hf-palette-detail">').text(result.detail));
                }

                item.data('index', i);
                this._list.append(item);
            }

            if (results.length === 0) {
                this._list.append($('<li class="hf-palette-empty">').text('Nothing found'));
            }
        };

        CommandPalette.prototype._move = function (delta) {
            if (this._results.length === 0) return;
            this._selected = (this._selected + delta + this._results.length) % this._results.length;
            this._list.find('.hf-palette-item').removeClass('selected').eq(this._selected).addClass('selected')[0].scrollIntoView({ block: 'nearest' });
        };

        CommandPalette.prototype._open = function () {
            var result = this._results[this._selected];
            if (result) window.location.href = result.url;
        };

        return CommandPalette;
    })();

    hangfire.LiveTable = (function () {
        var STORAGE_KEY = 'hangfire.liveTable';

        function LiveTable(page, config) {
            this._page = page;
            this._config = config;
            this._interval = Math.max((config.pollInterval || 2000) * 2, 2000);
            this._container = $('.js-jobs-list').first();
            this._basePath = (config.pollUrl || '').replace(/\/stats$/, '');

            if (!this._container.length) return;

            this._enabled = this._read();
            this._build();
            if (this._enabled) this._schedule();
        }

        LiveTable.prototype._read = function () {
            try { return window.localStorage.getItem(STORAGE_KEY) === 'true'; } catch (e) { return false; }
        };

        LiveTable.prototype._write = function (value) {
            try { window.localStorage.setItem(STORAGE_KEY, value ? 'true' : 'false'); } catch (e) { /* private mode */ }
        };

        LiveTable.prototype._build = function () {
            var self = this;
            var config = this._config;
            var toolbar = this._container.find('.btn-toolbar-top').first();
            if (!toolbar.length) return;

            var text = (config && config.text) || { refresh: 'Refresh', live: 'Live', liveHint: 'Refresh this list automatically' };

            this._refreshButton = $('<button type="button" class="btn btn-sm btn-default hf-refresh-button" title="' + text.refresh + '">')
                .append('<span class="glyphicon glyphicon-refresh"></span> ' + text.refresh)
                .on('click', function () {
                    self._refreshButton.addClass('spinning');
                    self._refresh(true);
                });

            toolbar.append(this._refreshButton);

            this._button = $('<button type="button" class="btn btn-sm btn-default hf-live-toggle" title="' + text.liveHint + '">')
                .append('<span class="hf-live-dot"></span> ' + text.live)
                .toggleClass('active', this._enabled)
                .on('click', function () {
                    self._enabled = !self._enabled;
                    self._write(self._enabled);
                    self._button.toggleClass('active', self._enabled);
                    if (self._enabled) self._schedule(); else clearTimeout(self._timer);
                });

            toolbar.append(this._button);
        };

        LiveTable.prototype._schedule = function () {
            var self = this;
            clearTimeout(this._timer);
            this._timer = setTimeout(function () { self._refresh(); }, this._interval);
        };

        LiveTable.prototype._refresh = function (manual) {
            var self = this;

            // Never pull the rug from under a selection or an open dialog, unless asked by hand
            if (!manual && (this._container.find('.js-jobs-list-checkbox:checked').length || $('.modal.in').length)) {
                this._schedule();
                return;
            }

            $.get(window.location.href)
                .done(function (html) {
                    var fresh = $($.parseHTML(html, document, false)).find('.js-jobs-list').first();

                    if (fresh.length) {
                        var toggle = self._button.detach();
                        var refresh = self._refreshButton.detach().removeClass('spinning');
                        self._container.html(fresh.html());
                        self._container.find('.btn-toolbar-top').first().append(refresh).append(toggle);
                        self._page.initJobsList(self._container[0]);
                        self._page.updateRelativeDates();
                        self._container.find('*[title]').tooltip();
                        self._button.addClass('hf-live-flash');
                        setTimeout(function () { self._button.removeClass('hf-live-flash'); }, 600);
                    }
                })
                .always(function () {
                    self._refreshButton.removeClass('spinning');
                    if (self._enabled) self._schedule();
                });
        };

        return LiveTable;
    })();
})(window.Hangfire = window.Hangfire || {});

$(function () {
    Hangfire.page = new Hangfire.Page(Hangfire.config);
});
