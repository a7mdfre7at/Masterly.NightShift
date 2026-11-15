(function (nightshift) {
    // The graphs' series, in the order of their datasets: failed and deleted are stacked under succeeded
    var SERIES = ['failed', 'deleted', 'succeeded'];

    // Gives a graph the colors of a scheme ("light" or "dark"); the realtime and history graphs share it
    var changeDatasetColorScheme = function (newColorScheme) {
        var colors = COLORS.get(newColorScheme);
        var datasets = this._chart.data.datasets;
        var scales = this._chart.options.scales;

        for (var i = 0; i < SERIES.length; i++) {
            datasets[i].backgroundColor = colors[SERIES[i]].backgroundColor;
            datasets[i].borderColor = colors[SERIES[i]].borderColor;
        }

        [scales.x, scales.y].forEach(function (scale) {
            scale.grid.color = colors.cartesianColor;
            scale.border.color = colors.axisColor;
            scale.ticks.color = colors.tickColor;
        });

        this._chart.update('none');
    };

    // The graphs' colours, read from the tokens of the theme the page shows (nightshift-tokens.css): the job states'
    // colour groups (succeeded, failed, deleted) and the outline and text roles for the axes. Only the succeeded
    // series has a line; the others are areas, so empty series draw no line along zero.
    var cssToken = function (name, fallback) {
        var value = window.getComputedStyle ? getComputedStyle(document.documentElement).getPropertyValue(name).trim() : '';
        return value || fallback;
    };

    var tokenRgba = function (name, alpha, fallback) {
        return 'rgba(' + cssToken(name, fallback) + ', ' + alpha + ')';
    };

    var COLORS = {
        get: function (scheme) {
            var dark = scheme === 'dark';
            return {
                cartesianColor: tokenRgba('--md-sys-color-on-surface-rgb', dark ? 0.1 : 0.08, '10, 37, 48'),
                axisColor: tokenRgba('--md-sys-color-outline-rgb', 0.6, '108, 121, 123'),
                tickColor: cssToken('--md-sys-color-on-surface-variant', '#3c494b'),
                failed: { backgroundColor: tokenRgba('--ns-state-failed-rgb', dark ? 0.45 : 0.7, '189, 0, 70'), borderColor: 'transparent' },
                deleted: { backgroundColor: tokenRgba('--ns-state-deleted-rgb', dark ? 0.35 : 0.4, '82, 97, 103'), borderColor: 'transparent' },
                succeeded: { backgroundColor: tokenRgba('--ns-state-succeeded-rgb', dark ? 0.35 : 0.45, '0, 109, 58'), borderColor: cssToken('--ns-state-succeeded', '#006d3a') }
            };
        }
    };

    if (typeof Chart !== 'undefined') {
        // Canvas fonts accept only real family names, so no ui-sans-serif or system-ui here
        Chart.defaults.font.family = '"IBM Plex Sans", "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';
    }

    // The options both graphs share: stacked areas without points or legend, a tooltip for every series at the
    // hovered time, colors of the current scheme
    var graphOptions = function (x, y) {
        var colors = COLORS.get(colorScheme);

        x.grid = { color: colors.cartesianColor };
        x.border = { color: colors.axisColor };
        x.ticks = $.extend({ maxRotation: 0, color: colors.tickColor }, x.ticks);

        y.grid = { color: colors.cartesianColor };
        y.border = { color: colors.axisColor };
        y.ticks = $.extend({ precision: 0, maxTicksLimit: 6, color: colors.tickColor }, y.ticks);
        y.beginAtZero = true;
        y.stacked = true;

        return {
            scales: { x: x, y: y },
            elements: { line: { tension: 0 }, point: { radius: 0 } },
            plugins: {
                legend: { display: false },
                tooltip: { mode: 'index', intersect: false }
            }
        };
    };

    var graphDatasets = function (labels, data) {
        var colors = COLORS.get(colorScheme);

        return SERIES.map(function (name) {
            return {
                label: labels[name],
                borderColor: colors[name].borderColor,
                backgroundColor: colors[name].backgroundColor,
                borderWidth: name === 'succeeded' ? 3 : 2,
                fill: 'origin',
                data: data ? data[name] : []
            };
        });
    };

    // A value as one segment of a dashboard path, written as RouteValues.Encode writes it on the server.
    // ASP.NET Core decodes the path except "%2F", so encodeURIComponent alone gave "a/b" and the literal
    // "a%2Fb" the same path: "%", "/" and "~" are written "~25", "~2F" and "~7E" first.
    nightshift.routeValue = function (value) {
        return encodeURIComponent(String(value).replace(/[~%\/]/g, function (c) {
            return c === '~' ? '~7E' : c === '%' ? '~25' : '~2F';
        }));
    };

    // jQuery 4 has no $.trim: a field's value without the white space around it, or '' for none
    var trim = function (value) {
        return value === null || value === undefined ? '' : String(value).trim();
    };

    // Encodes the characters WebUtility.HtmlEncode encodes in the ASCII range, as the server does
    var escapeAttribute = function (value) {
        return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    };

    var hasOwn = function (object, key) {
        return Object.prototype.hasOwnProperty.call(object, key);
    };

    // The sprite's name for an icon, as DashboardIcons.Resolve gives it on the server: a Bootstrap Icons name the
    // sprite holds (with or without "bi-"), or the icon that replaced a Glyphicon name (with or without
    // "glyphicon-"); null for any other name. The names come with the script bundle (NightShift._icons).
    var resolveIcon = function (name) {
        var icons = nightshift._icons || { names: [], glyphicons: {} };
        if (!icons.lookup) {
            icons.lookup = {};
            for (var i = 0; i < icons.names.length; i++) icons.lookup[icons.names[i]] = true;
        }

        var trimmed = trim(name);
        if (trimmed.indexOf('bi-') === 0) trimmed = trimmed.substring(3);
        if (!trimmed) return null;
        if (hasOwn(icons.lookup, trimmed)) return trimmed;

        var glyphicon = trimmed.indexOf('glyphicon-') === 0 ? trimmed.substring('glyphicon-'.length) : trimmed;
        return hasOwn(icons.glyphicons, glyphicon) ? icons.glyphicons[glyphicon] : null;
    };

    // An icon of the dashboard's sprite (Bootstrap Icons) as HTML, the same HTML Html.Icon writes on the server: an
    // SVG that takes the size and color of the text around it and is hidden from screen readers. name is a Bootstrap
    // Icons name the sprite holds, such as "trash-fill", or a Glyphicon name of earlier versions, such as "trash";
    // any other name is written as it is (without spaces around it or "bi-") and draws nothing. cssClass, when
    // given, is added to "bi".
    nightshift.icon = function (name, cssClass) {
        var url = $('#nightshiftConfig').attr('data-icons-url') || '';
        var symbol = resolveIcon(name);

        if (symbol === null) {
            symbol = trim(name);
            if (symbol.indexOf('bi-') === 0) symbol = symbol.substring(3);
        }

        return '<svg class="bi' + (trim(cssClass) ? ' ' + escapeAttribute(cssClass) : '') + '" aria-hidden="true"><use href="'
            + escapeAttribute(url) + '#bi-' + escapeAttribute(symbol) + '"></use></svg>';
    };

    // The busy state of a button while its request runs: it shows a circular progress indicator and its
    // data-loading-text (when it has one), and says aria-busy and aria-disabled. It isn't disabled, so it keeps the
    // focus; the click guard below ignores clicks on it. Afterwards it gets its content back.
    nightshift.setLoading = function (element, loading) {
        return $(element).each(function () {
            var button = $(this);

            if (loading) {
                if (button.data('nightshift-reset-html') === undefined) {
                    button.data('nightshift-reset-html', button.html());
                }

                var text = button.attr('data-loading-text');
                nightshift.disposeTooltips(button.children());
                if (text) {
                    button.text(text);
                }

                button.prepend('<span class="ns-progress-circular" aria-hidden="true"></span>')
                    .attr({ 'aria-busy': 'true', 'aria-disabled': 'true' });
            } else {
                var html = button.data('nightshift-reset-html');
                if (html !== undefined) {
                    nightshift.disposeTooltips(button.children());
                    button.html(html).removeData('nightshift-reset-html');
                }

                button.prop('disabled', false).removeClass('disabled').removeAttr('aria-busy').removeAttr('aria-disabled');
            }
        });
    };

    // A control that says aria-disabled (a busy button, the previous link of the first page) stays focusable, so
    // screen readers find it, but does nothing: its clicks stop here, before any handler of the page sees them
    document.addEventListener('click', function (e) {
        var target = e.target && e.target.closest ? e.target.closest('[aria-disabled="true"]') : null;
        if (target) {
            e.preventDefault();
            e.stopImmediatePropagation();
        }
    }, true);

    // The text of a #nightshiftConfig data-text-* attribute (read with .attr: .data() would turn "{0}" or a number
    // into something else), or the fallback
    var configText = function (name, fallback) {
        var value = $('#nightshiftConfig').attr('data-text-' + name);
        return value === undefined || value === null || value === '' ? fallback : value;
    };

    // A resource text with {0}, {1}... replaced by the arguments
    var format = function (text) {
        var args = Array.prototype.slice.call(arguments, 1);
        return String(text).replace(/\{(\d+)\}/g, function (match, index) {
            var value = args[parseInt(index, 10)];
            return value === undefined ? match : String(value);
        });
    };

    nightshift.format = format;

    // Whether the user asked for less motion
    var reducedMotion = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : { matches: false };
    nightshift.prefersReducedMotion = function () { return !!reducedMotion.matches; };
    if (reducedMotion.matches && $.fx) $.fx.off = true;

    // A Bootstrap tooltip for every element with a title in the container (the whole page without one), for
    // content added after the page loaded too; an element that has one keeps it
    nightshift.tooltips = function (container) {
        if (!window.bootstrap || !bootstrap.Tooltip) return;

        $(container || document).find('[title]').addBack('[title]').each(function () {
            tooltipRole(this);
            bootstrap.Tooltip.getOrCreateInstance(this);
        });
    };

    // Bootstrap names an element without text after its tooltip (aria-label), which a plain div or span may not
    // carry: such an element is an image of what its tooltip says
    var tooltipRole = function (element) {
        if (/^(DIV|SPAN|I)$/.test(element.tagName) && !element.getAttribute('role') && !element.getAttribute('aria-label') && !trim(element.textContent)) {
            element.setAttribute('role', 'img');
        }
    };

    nightshift.tooltipRoles = function (container) {
        $(container || document).find('[title], [data-bs-original-title]').each(function () { tooltipRole(this); });
    };

    // Disposes the Bootstrap tooltips and popovers of the container (the element or elements given, and everything
    // inside them; the whole page without one), and takes away the ones they show. Call it before content with
    // tooltips is replaced or removed: Bootstrap keeps its instances in a map of its own that jQuery's cleanup
    // doesn't reach, so the removed elements would stay in memory, and a tooltip shown at that moment (Bootstrap
    // puts it in <body>) would stay on the screen.
    nightshift.disposeTooltips = function (container) {
        if (!window.bootstrap || !bootstrap.Tooltip) return;

        var dispose = function (element) {
            var tooltip = bootstrap.Tooltip.getInstance(element);
            if (tooltip) tooltip.dispose();

            var popover = bootstrap.Popover ? bootstrap.Popover.getInstance(element) : null;
            if (popover) popover.dispose();
        };

        $(container || document).each(function () {
            dispose(this);

            if (this.querySelectorAll) {
                var elements = this.querySelectorAll('*');
                for (var i = 0; i < elements.length; i++) {
                    dispose(elements[i]);
                }
            }
        });
    };

    // Changes an element's title, also when its Bootstrap tooltip took the title over
    nightshift.setTitle = function (element, text) {
        return $(element).each(function () {
            var tooltip = window.bootstrap && bootstrap.Tooltip ? bootstrap.Tooltip.getInstance(this) : null;

            if (tooltip) {
                this.setAttribute('data-bs-original-title', text);
                tooltip.setContent({ '.tooltip-inner': text });
            } else {
                this.setAttribute('title', text);
            }
        });
    };

    nightshift.config = {
        pollInterval: $("#nightshiftConfig").data("pollinterval"),
        pollUrl: $("#nightshiftConfig").data("pollurl"),
        text: {
            refresh: configText('refresh', 'Refresh'),
            live: configText('live', 'Live'),
            liveHint: configText('live-hint', 'Refresh this list automatically'),
            listResult: configText('list-result', '{0} jobs changed.'),
            listRemaining: configText('list-remaining', 'There is more to do (up to {0} jobs). Run it again to continue.'),
            requestFailed: configText('request-failed', 'The request failed:'),
            statusLive: configText('status-live', 'Live'),
            statusLiveHint: configText('status-live-hint', ''),
            statusOffline: configText('status-offline', 'Offline – retrying'),
            statusOfflineHint: configText('status-offline-hint', ''),
            closeMenu: configText('close-menu', 'Close the menu'),
            cancel: configText('cancel', 'Cancel'),
            confirm: configText('confirm', 'Confirm'),
            close: configText('close', 'Close'),
            dismiss: configText('dismiss', 'Dismiss'),
            retry: configText('retry', 'Retry'),
            discardTitle: configText('discard-title', 'Discard changes?'),
            discardBody: configText('discard-body', 'What you changed in this dialog is lost.'),
            discard: configText('discard', 'Discard'),
            keepEditing: configText('keep-editing', 'Keep editing'),
            typeToConfirm: configText('type-to-confirm', 'Type {0} to confirm'),
            done: configText('done', '{0}: done.'),
            actionFailed: configText('action-failed', '{0} failed: {1}'),
            serverResponded: configText('server-responded', 'The server responded with {0}.'),
            noAnswer: configText('no-answer', "The server didn't answer."),
            viewJob: configText('view-job', 'View job {0}'),
            copied: configText('copied', 'Copied to the clipboard'),
            listUpdated: configText('list-updated', 'List updated'),
            listUpdatedNew: configText('list-updated-new', 'List updated · {0} new'),
            selectedCount: configText('selected-count', '{0} selected'),
            clearSelection: configText('clear-selection', 'Clear selection'),
            shortcutConfirm: configText('shortcut-confirm', '{0}: {1} selected. Go ahead?'),
            paletteLabel: configText('palette-label', 'Search and commands'),
            palettePlaceholder: configText('palette-placeholder', 'Jump to a page, a job id, a recurring job or a tag…'),
            paletteResults: configText('palette-results', 'Results'),
            paletteNothing: configText('palette-nothing', 'Nothing found'),
            paletteCount: configText('palette-count', '{0} results'),
            paletteCountOne: configText('palette-count-one', '1 result'),
            paletteHint: configText('palette-hint', '{0} move · {1} open · {2} close · {3} commands'),
            paletteHintAnywhere: configText('palette-hint-anywhere', '{0} or {1} opens this anywhere'),
            palettePages: configText('palette-pages', 'Pages'),
            paletteCommands: configText('palette-commands', 'Commands'),
            paletteJobs: configText('palette-jobs', 'Jobs'),
            paletteRecurring: configText('palette-recurring', 'Recurring jobs'),
            paletteTags: configText('palette-tags', 'Tags'),
            shortcuts: configText('shortcuts', 'Keyboard shortcuts'),
            shortcutSearch: configText('shortcut-search', 'Search, jump to a page, run a command (type >)'),
            shortcutGo: configText('shortcut-go', 'Go to home, jobs, processing, failed, recurring, servers, insights, catalog, tags, batches, audit'),
            shortcutMove: configText('shortcut-move', 'Move down / up a job list'),
            shortcutSelect: configText('shortcut-select', 'Select the focused job'),
            shortcutOpen: configText('shortcut-open', 'Open the focused job'),
            shortcutRun: configText('shortcut-run', 'Run the main command of the list for the selected items, after a confirmation'),
            shortcutDelete: configText('shortcut-delete', 'Delete the selected jobs'),
            shortcutHelp: configText('shortcut-help', 'This help'),
            shortcutSingleKey: configText('shortcut-single-key', 'Single-key shortcuts (j, k, x, r, d, g, / and ?). Ctrl K works either way.')
        },
        locale: document.documentElement.lang,
        darkMode: $("#nightshiftConfig").data("darkmode"),
        readOnly: $("#nightshiftConfig").attr("data-readonly") === "true"
    };

    var TEXT = nightshift.config.text;

    // The effective color scheme: the chosen theme, or the system's when none is chosen
    var effectiveScheme = function () {
        if (!nightshift.config.darkMode) return "light";
        var chosen = document.documentElement.getAttribute('data-theme');
        if (chosen === 'dark' || chosen === 'light') return chosen;
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? "dark" : "light";
    };

    var colorScheme = effectiveScheme();

    var applySchemeToCharts = function () {
        colorScheme = effectiveScheme();
        if (nightshift.page && nightshift.page.realtimeGraph) nightshift.page.realtimeGraph.changeDatasetColorScheme(colorScheme);
        if (nightshift.page && nightshift.page.historyGraph) nightshift.page.historyGraph.changeDatasetColorScheme(colorScheme);
    };

    if (nightshift.config.darkMode && window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', applySchemeToCharts);
    }

    $(document).on('nightshift:theme', applySchemeToCharts);

    nightshift.Theme = (function () {
        var STORAGE_KEY = 'nightshift.theme';

        function Theme() {
            this._buttons = $('.js-theme button[data-theme-choice]');
            this._stylesheet = document.getElementById('hfDarkStylesheet');

            // Without dark mode the page stays light, whatever another dashboard of the site saved
            if (!nightshift.config.darkMode) return;

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

            this._buttons.removeClass('active').attr('aria-pressed', 'false')
                .filter('[data-theme-choice="' + choice + '"]').addClass('active').attr('aria-pressed', 'true');

            if (notify) {
                $(document).trigger('nightshift:theme');
            }
        };

        return Theme;
    })();

    // A table that fits its .table-responsive box needs no sideways scrolling: the box gets ns-table-fits, so on large
    // windows its header can stick under the app bar (CSS). Measured again when the window or the list changes.
    nightshift.markFittingTables = function (container) {
        $(container || document).find('.table-responsive').each(function () {
            var table = $(this).children('table')[0];
            if (!table) return;
            this.classList.remove('ns-table-fits');
            var fits = table.scrollWidth <= this.clientWidth + 1;
            this.classList.toggle('ns-table-fits', fits);
            // A box that scrolls sideways can be scrolled with the keyboard
            if (fits) this.removeAttribute('tabindex'); else this.setAttribute('tabindex', '0');
        });
    };

    // The focusable elements of a container that a user can reach with Tab, in order
    var tabbables = function (container) {
        return $(container).find('a[href], button, input, select, textarea, [tabindex]').filter(function () {
            if (this.disabled || this.tabIndex < 0 || this.getAttribute('tabindex') === '-1') return false;
            if (this.type === 'hidden' || this.closest('[inert]')) return false;
            return this.offsetWidth > 0 || this.offsetHeight > 0 || this.getClientRects().length > 0;
        });
    };

    // Keeps Tab and Shift+Tab inside a container: from its last control to its first and back
    var trapTab = function (e, container) {
        if (e.key !== 'Tab') return;
        var items = tabbables(container);
        if (!items.length) { e.preventDefault(); return; }

        var first = items[0];
        var last = items[items.length - 1];
        var active = document.activeElement;

        if (e.shiftKey && (active === first || !$.contains(container, active))) {
            e.preventDefault();
            last.focus();
        } else if (!e.shiftKey && (active === last || !$.contains(container, active))) {
            e.preventDefault();
            first.focus();
        }
    };

    var focusBack = function (target) {
        if (target && target !== document.body && document.contains(target) && typeof target.focus === 'function') target.focus();
    };

    nightshift.trapTab = trapTab;

    // The navigation drawer and the top app bar. From 1240px the drawer stands beside the content; from 600 to 1239px
    // a rail does, and the menu button opens the drawer over it as a modal; below 600px the drawer is modal only, and
    // inert while closed. The modal drawer takes the focus to its first item, keeps Tab inside, and closes with Esc,
    // the scrim or its close button, giving the focus back to the menu button.
    nightshift.Shell = (function () {
        function Shell() {
            var self = this;
            this._body = $('body');
            this._sidebar = $('#hfSidebar');
            this._toggle = $('.js-sidebar-toggle');
            this._scrim = $('.js-sidebar-scrim');
            this._main = $('.hf-main');
            this._standard = window.matchMedia ? window.matchMedia('(min-width: 1240px)') : { matches: true, addEventListener: function () { } };
            this._compact = window.matchMedia ? window.matchMedia('(max-width: 599.98px)') : { matches: false, addEventListener: function () { } };

            if (!this._sidebar.length) return;

            this._toggle.on('click', function (e) {
                e.stopPropagation();
                if (self.isOpen()) self.close(true); else self.open();
            });

            $(document).on('click', '.js-sidebar-close', function () { self.close(true); });
            this._scrim.on('click', function () { self.close(true); });

            this._sidebar.on('keydown', function (e) {
                if (!self.isOpen()) return;
                if (e.key === 'Escape') { e.preventDefault(); self.close(true); return; }
                trapTab(e, self._sidebar[0]);
            });

            var sync = function () { self._sync(); };
            this._standard.addEventListener('change', sync);
            this._compact.addEventListener('change', sync);
            this._sync();

            $(document).on('click', '.js-page-refresh', function () {
                var button = $(this);
                if (button.attr('aria-busy') === 'true') return;
                button.addClass('spinning').attr('aria-busy', 'true');
                // Offline, Refresh asks the server again first: a page reloaded while the server is gone would be an
                // error page. Online, it reloads at once.
                var poller = nightshift.page && nightshift.page._poller;
                if (!Shell.offline || !poller) { window.location.reload(); return; }
                poller.pollNow().then(function () {
                    window.location.reload();
                }, function () {
                    button.removeClass('spinning').removeAttr('aria-busy');
                });
            });
        }

        Shell.prototype.mode = function () {
            return this._standard.matches ? 'standard' : this._compact.matches ? 'modal' : 'rail';
        };

        Shell.prototype.isOpen = function () {
            return this._body.hasClass('hf-sidebar-open');
        };

        Shell.prototype.open = function () {
            if (this.mode() === 'standard') return;
            this._returnFocus = document.activeElement;
            this._body.addClass('hf-sidebar-open');
            this._sync();
            var first = this._sidebar.find('.hf-nav-item').filter(':visible').first();
            (first.length ? first : this._sidebar.find('.js-sidebar-close')).trigger('focus');
        };

        Shell.prototype.close = function (returnFocus) {
            if (!this.isOpen()) return;
            this._body.removeClass('hf-sidebar-open');
            this._sync();
            if (returnFocus) focusBack(this._toggle.filter(':visible')[0] || this._returnFocus);
        };

        Shell.prototype._sync = function () {
            var mode = this.mode();
            if (mode === 'standard' && this.isOpen()) this._body.removeClass('hf-sidebar-open');

            var open = this.isOpen();
            var sidebar = this._sidebar[0];

            sidebar.inert = mode === 'modal' && !open;
            if (open) {
                this._sidebar.attr({ role: 'dialog', 'aria-modal': 'true', 'aria-label': this._sidebar.attr('data-label') || null });
            } else {
                this._sidebar.removeAttr('role').removeAttr('aria-modal').removeAttr('aria-label');
            }

            if (this._main.length) this._main[0].inert = open;
            this._scrim.prop('hidden', !open);
            this._toggle.attr('aria-expanded', open ? 'true' : 'false');
        };

        // The status chip of the app bar: Live, or "Offline – retrying" while the polls fail
        Shell.offline = false;

        Shell.setOnline = function (online) {
            $('.js-pulse-dot').not('.js-activity-dot').toggleClass('offline', !online);
            if (Shell.offline === !online) return;
            Shell.offline = !online;

            var chip = $('.js-status');
            chip.toggleClass('offline', !online);
            chip.find('.js-status-text').text(online ? TEXT.statusLive : TEXT.statusOffline);
            nightshift.setTitle(chip, online ? TEXT.statusLiveHint : TEXT.statusOfflineHint);
        };

        return Shell;
    })();

    // An error that keeps the page from working, in the alert under the app bar. A failed request shows an error
    // snackbar instead (showRequestError).
    nightshift.ErrorAlert = (function () {
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
            this._reveal();
        };

        // Like show, for text that may come from the server: it is never read as HTML
        ErrorAlert.prototype.showText = function () {
            this._errorAlertTitle.text(this._title);
            this._errorAlertMessage.text(this._message);
            this._reveal();
        };

        ErrorAlert.prototype._reveal = function () {
            this._errorAlert.removeClass('display-none').show();
        };

        return ErrorAlert;
    })();

    // Why a request failed, in one sentence: the status, and the server's message when it sent plain text or JSON with
    // an "error"
    nightshift.describeRequestError = function (xhr) {
        var detail = '';

        if (xhr && xhr.responseJSON && typeof xhr.responseJSON.error === 'string') {
            detail = xhr.responseJSON.error;
        } else if (xhr && typeof xhr.responseText === 'string' && /^text\/plain/i.test(xhr.getResponseHeader('Content-Type') || '')) {
            detail = xhr.responseText;
        }

        if (detail.length > 300) detail = detail.substring(0, 300) + '…';

        var status = xhr && xhr.status
            ? format(TEXT.serverResponded, String(xhr.status) + (xhr.statusText ? ' (' + xhr.statusText + ')' : ''))
            : TEXT.noAnswer;

        return status + (detail ? ' ' + detail : '');
    };

    // Tells the user why a request failed, in an error snackbar naming the action: the status, and the server's
    // message. retry, when given, is run by the snackbar's Retry button. Used by every button that changes
    // something, so a failure is never silent.
    nightshift.showRequestError = function (xhr, title, retry) {
        var action = trim(title) ? trim(title).replace(/[:：]\s*$/, '') : trim(TEXT.requestFailed).replace(/[:：]\s*$/, '');
        nightshift.snackbar.show({
            text: format(TEXT.actionFailed, action, nightshift.describeRequestError(xhr)),
            kind: 'error',
            duration: 10000,
            action: typeof retry === 'function' ? { label: TEXT.retry, run: retry } : null
        });
    };

    // MD3 snackbars: short messages at the bottom of the window, one at a time, in a polite status region. Each
    // stays 4 to 10 seconds (longer with an action), and while the pointer or the focus is on it. An action is a
    // link ({ label, href }) or a function ({ label, run }).
    nightshift.snackbar = (function () {
        var STORAGE_KEY = 'nightshift.snackbar';
        var queue = [];
        var current = null;

        var host = function () {
            var element = $('.js-snackbar-host');
            if (!element.length) element = $('<div class="ns-snackbar-host js-snackbar-host" role="status" aria-live="polite">').appendTo('body');
            return element;
        };

        var next = function () {
            if (current || !queue.length) return;

            var options = queue.shift();
            var duration = Math.min(Math.max(options.duration || (options.action ? 8000 : 5000), 4000), 10000);
            var element = $('<div class="ns-snackbar">').toggleClass('ns-snackbar-error', options.kind === 'error');

            if (options.kind === 'error') element.append(nightshift.icon('exclamation-circle-fill'));
            else if (options.kind === 'warning') element.append(nightshift.icon('exclamation-triangle-fill'));

            element.append($('<span class="ns-snackbar-text">').text(options.text));

            if (options.action && options.action.label) {
                var action = options.action.href
                    ? $('<a class="ns-snackbar-action">').attr('href', options.action.href)
                    : $('<button type="button" class="ns-snackbar-action">');
                action.text(options.action.label).on('click', function () {
                    if (typeof options.action.run === 'function') options.action.run();
                    hide();
                });
                element.append(action);
            }

            element.append($('<button type="button" class="ns-icon-button ns-icon-button-sm ns-snackbar-close">')
                .attr('aria-label', TEXT.dismiss).html(nightshift.icon('x-lg')).on('click', function () { hide(); }));

            var timer = null;
            var held = false;
            var start = function () { clearTimeout(timer); timer = setTimeout(function () { if (!held) hide(); }, duration); };

            element.on('mouseenter focusin', function () { held = true; clearTimeout(timer); })
                .on('mouseleave focusout', function (e) {
                    if (e.type === 'focusout' && element[0].contains(e.relatedTarget)) return;
                    held = false;
                    start();
                });

            current = { element: element, timer: function () { clearTimeout(timer); } };
            host().append(element);
            setTimeout(function () { element.addClass('show'); }, 10);
            start();
        };

        var hide = function () {
            if (!current) return;
            var element = current.element;
            current.timer();
            current = null;
            element.removeClass('show');
            setTimeout(function () { element.remove(); next(); }, nightshift.prefersReducedMotion() ? 0 : 200);
        };

        return {
            show: function (options) {
                if (typeof options === 'string') options = { text: options };
                if (!options || !options.text) return;
                queue.push(options);
                next();
            },
            hide: hide,
            // Shows the message on the next page this tab loads (a command that reloads the page says what it did)
            showAfterReload: function (options) {
                try {
                    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify({
                        text: options.text,
                        kind: options.kind || null,
                        action: options.action && options.action.href ? { label: options.action.label, href: options.action.href } : null
                    }));
                } catch (e) { /* no storage: the reload says nothing */ }
            },
            restore: function () {
                var saved = null;
                try {
                    saved = window.sessionStorage.getItem(STORAGE_KEY);
                    window.sessionStorage.removeItem(STORAGE_KEY);
                } catch (e) { return; }
                if (!saved) return;
                try { this.show(JSON.parse(saved)); } catch (e) { /* not ours */ }
            }
        };
    })();

    // A short message at the bottom: a snackbar; kind is "succeeded", "failed" or "warning"
    nightshift.showToast = function (text, kind) {
        nightshift.snackbar.show({ text: text, kind: kind === 'failed' ? 'error' : kind === 'warning' ? 'warning' : null });
    };

    // A polite announcement for screen readers, through one status region of the page
    nightshift.announce = function (text) {
        var region = $('.js-announcer');
        if (!region.length) return;
        region.text('');
        setTimeout(function () { region.text(text); }, 50);
    };

    // The dashboard's dialogs: every Bootstrap modal of the page gives the focus back to the control that opened it
    // and keeps Tab inside; a dialog with a form asks "Discard changes?" before it closes over unsaved input (and
    // ignores clicks on its scrim meanwhile). Below 600px a dialog fills the window (CSS).
    nightshift.Dialogs = (function () {
        function Dialogs() {
            $(document).on('show.bs.modal', '.modal', function (e) {
                var modal = $(this);
                if (!modal.data('ns-opener')) modal.data('ns-opener', e.relatedTarget || document.activeElement);
                Dialogs.setDirty(this, false);
            });

            $(document).on('hidden.bs.modal', '.modal', function () {
                var modal = $(this);
                var opener = modal.data('ns-opener');
                modal.removeData('ns-opener');
                if (!modal.data('ns-keep-focus')) focusBack(opener);
                modal.removeData('ns-keep-focus');
            });

            $(document).on('keydown', '.modal.show', function (e) {
                if (e.key === 'Tab') trapTab(e, this);
            });

            // Input in a dialog's form makes it dirty; a submit means the input was used
            $(document).on('input change', '.modal:not(.ns-dialog-basic) form :input', function () {
                Dialogs.setDirty($(this).closest('.modal')[0], true);
            });

            $(document).on('submit', '.modal form', function () {
                Dialogs.setDirty($(this).closest('.modal')[0], false);
            });

            $(document).on('hide.bs.modal', '.modal', function (e) {
                var modal = this;
                if (!$(modal).data('ns-dirty')) return;

                e.preventDefault();
                nightshift.confirm({
                    title: TEXT.discardTitle,
                    body: TEXT.discardBody,
                    confirmLabel: TEXT.discard,
                    cancelLabel: TEXT.keepEditing,
                    destructive: true
                }).then(function (discard) {
                    if (!discard) return;
                    Dialogs.setDirty(modal, false);
                    bootstrap.Modal.getOrCreateInstance(modal).hide();
                });
            });
        }

        Dialogs.setDirty = function (modal, dirty) {
            if (!modal) return;
            $(modal).data('ns-dirty', !!dirty);
            var instance = window.bootstrap && bootstrap.Modal ? bootstrap.Modal.getInstance(modal) : null;
            // A static backdrop: a click beside a dirty dialog doesn't close it
            if (instance && instance._config) {
                if (instance._ns_backdrop === undefined) instance._ns_backdrop = instance._config.backdrop;
                instance._config.backdrop = dirty ? 'static' : instance._ns_backdrop;
            }
        };

        return Dialogs;
    })();

    // The confirmation dialog: NightShift.confirm({ title, body, confirmLabel, cancelLabel, destructive,
    // typeToConfirm }) shows a headline, what happens, Cancel and the confirming button (in the error colour when
    // destructive), and resolves its Promise with true or false. With typeToConfirm the confirming button works only
    // once that text is typed.
    nightshift.confirm = (function () {
        var modal = null;
        var pending = null;

        var build = function () {
            modal = $('<div class="modal fade ns-dialog-basic" id="nsConfirmDialog" tabindex="-1" aria-labelledby="nsConfirmTitle" aria-describedby="nsConfirmBody" aria-hidden="true">')
                .append($('<div class="modal-dialog modal-dialog-centered">')
                    .append($('<div class="modal-content">')
                        .append($('<div class="modal-header">').append('<h2 class="modal-title" id="nsConfirmTitle"></h2>'))
                        .append($('<div class="modal-body">')
                            .append('<p class="ns-dialog-body" id="nsConfirmBody"></p>')
                            .append($('<div class="ns-field ns-dialog-type">')
                                .append('<label for="nsConfirmType" id="nsConfirmTypeLabel"></label>')
                                .append('<input type="text" class="form-control" id="nsConfirmType" autocomplete="off" spellcheck="false">')))
                        .append($('<div class="modal-footer">')
                            .append('<button type="button" class="btn ns-btn-text js-confirm-cancel"></button>')
                            .append('<button type="button" class="btn js-confirm-ok"></button>'))));

            $('body').append(modal);

            modal.on('click', '.js-confirm-cancel', function () { finish(false); });
            modal.on('click', '.js-confirm-ok', function () {
                if ($(this).attr('aria-disabled') === 'true') return;
                finish(true);
            });
            modal.on('input', '#nsConfirmType', function () { updateTyped(); });
            modal.on('keydown', '#nsConfirmType', function (e) {
                if (e.key === 'Enter') { e.preventDefault(); modal.find('.js-confirm-ok').trigger('click'); }
            });
            modal.on('hidden.bs.modal', function () {
                if (pending) { var resolve = pending.resolve; pending = null; resolve(false); }
            });
        };

        var updateTyped = function () {
            var expected = pending && pending.options.typeToConfirm;
            if (!expected) return;
            var ok = trim(modal.find('#nsConfirmType').val()).toLowerCase() === trim(expected).toLowerCase();
            modal.find('.js-confirm-ok').attr('aria-disabled', ok ? null : 'true');
            modal.find('#nsConfirmType').attr('aria-invalid', null);
        };

        var finish = function (result) {
            if (!pending) return;
            var resolve = pending.resolve;
            pending = null;
            resolve(result);
            bootstrap.Modal.getOrCreateInstance(modal[0]).hide();
        };

        return function (options) {
            options = options || {};
            if (!window.bootstrap || !bootstrap.Modal) return Promise.resolve(window.confirm(options.title || ''));
            if (!modal) build();
            if (pending) { var previous = pending.resolve; pending = null; previous(false); }

            return new Promise(function (resolve) {
                pending = { resolve: resolve, options: options };

                modal.toggleClass('ns-dialog-destructive', !!options.destructive).attr('role', 'alertdialog');
                modal.find('#nsConfirmTitle').text(options.title || TEXT.confirm);
                modal.find('#nsConfirmBody').text(options.body || '').toggle(!!options.body);
                modal.attr('aria-describedby', options.body ? 'nsConfirmBody' : null);

                var typed = modal.find('.ns-dialog-type').toggle(!!options.typeToConfirm);
                if (options.typeToConfirm) {
                    var label = modal.find('#nsConfirmTypeLabel').empty();
                    var parts = String(TEXT.typeToConfirm).split('{0}');
                    label.append(document.createTextNode(parts[0] || '')).append($('<code>').text(options.typeToConfirm)).append(document.createTextNode(parts[1] || ''));
                    typed.find('input').val('');
                }

                modal.find('.js-confirm-cancel').text(options.cancelLabel || TEXT.cancel);
                modal.find('.js-confirm-ok').text(options.confirmLabel || TEXT.confirm)
                    .toggleClass('ns-btn-error', !!options.destructive).toggleClass('ns-btn-filled', !options.destructive)
                    .attr('aria-disabled', options.typeToConfirm ? 'true' : null);

                var opener = document.activeElement;
                modal.data('ns-opener', opener);
                modal.one('shown.bs.modal', function () {
                    // The safe choice has the focus, or the field to type in
                    (options.typeToConfirm ? modal.find('#nsConfirmType') : modal.find(options.destructive ? '.js-confirm-cancel' : '.js-confirm-ok')).trigger('focus');
                });

                bootstrap.Modal.getOrCreateInstance(modal[0], { backdrop: true, keyboard: true }).show();
            });
        };
    })();

    // The confirmation a command button asks for, from its data-confirm attributes: data-confirm (the headline,
    // where {0} is the number of selected rows), data-confirm-one (the headline for one row), data-confirm-body,
    // data-confirm-label (else the button's own text), data-confirm-destructive (or a btn-death / btn-danger button)
    // and data-confirm-type. Resolves true at once for a button without data-confirm.
    nightshift.confirmButton = function (button, count) {
        button = $(button);
        var title = button.attr('data-confirm');
        if (!title) return Promise.resolve(true);

        if (count === 1 && button.attr('data-confirm-one')) title = button.attr('data-confirm-one');
        var label = button.attr('data-confirm-label') || trim(button.clone().find('.visually-hidden, .ns-progress-circular').remove().end().text());

        return nightshift.confirm({
            title: format(title, count === undefined ? '' : count),
            body: button.attr('data-confirm-body') || null,
            confirmLabel: label || null,
            destructive: button.attr('data-confirm-destructive') === 'true' || button.is('.btn-death, .btn-danger, .ns-btn-error'),
            typeToConfirm: button.attr('data-confirm-type') || null
        });
    };

    // The name of a command button for messages: its text without icons or progress, or the accessible name of an
    // icon button
    var commandName = function (button) {
        var text = trim($(button).clone().find('.ns-progress-circular, .visually-hidden').remove().end().text()).replace(/\s+/g, ' ');
        return text || trim($(button).attr('aria-label'));
    };

    // The side sheet: an MD3 modal side sheet at the end of the window (full screen below 600px) for the details of
    // an item without leaving the page. NightShift.sheet.open({ title, overline, content, url, wide, opener,
    // onClose }) shows it with the title in its h2 and content (HTML, a node or a jQuery object) or the HTML of url,
    // loaded with a skeleton meanwhile; an element of the content with the class js-sheet-actions has its children
    // moved to the sheet's footer. The rest of the page is inert while it is open; Tab stays inside, Esc, the close
    // button and the scrim close it (NightShift.sheet.close()), and the focus goes back to the opener (the focused
    // element by default). A button with the class js-sheet-open opens it from its data-sheet-title,
    // data-sheet-overline and data-sheet-url, or the template its data-sheet-content selector names, or else the
    // template.js-sheet-template of its row.
    nightshift.sheet = (function () {
        var sheet = null;
        var scrim = null;
        var state = null;
        var hideTimer = null;
        var request = null;

        var build = function () {
            scrim = $('<div class="ns-sheet-scrim js-sheet-scrim" hidden>').appendTo('body').on('click', function () { close(); });
            sheet = $('<div class="ns-sheet" role="dialog" aria-modal="true" aria-labelledby="nsSheetTitle" tabindex="-1" hidden>')
                .append($('<div class="ns-sheet-header">')
                    .append($('<div class="ns-sheet-heading">')
                        .append('<p class="ns-sheet-overline" id="nsSheetOverline" hidden></p>')
                        .append('<h2 class="ns-sheet-title" id="nsSheetTitle"></h2>'))
                    .append($('<button type="button" class="ns-icon-button js-sheet-close">')
                        .attr({ 'aria-label': TEXT.close, title: TEXT.close }).html(nightshift.icon('x-lg'))))
                // The body scrolls on its own, so it takes the focus for the keyboard to scroll it
                .append('<div class="ns-sheet-body" tabindex="0"></div>')
                .append('<div class="ns-sheet-actions" hidden></div>')
                .appendTo('body');

            sheet.on('click', '.js-sheet-close', function () { close(); });
            sheet.on('keydown', function (e) {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    e.stopPropagation();
                    close();
                    return;
                }
                trapTab(e, sheet[0]);
            });
        };

        var setContent = function (content) {
            var body = sheet.find('.ns-sheet-body');
            var actions = sheet.find('.ns-sheet-actions');
            nightshift.disposeTooltips(body);
            nightshift.disposeTooltips(actions);
            body.empty().append(content === undefined || content === null ? '' : content);

            var footer = body.find('.js-sheet-actions').first();
            actions.empty().append(footer.children()).prop('hidden', !footer.length || !actions.children().length);
            footer.remove();

            if (nightshift.page) nightshift.page.updateRelativeDates();
            nightshift.tooltips(sheet);
            nightshift.fields(sheet);
        };

        var skeleton = function () {
            var lines = $('<div class="ns-sheet-skeleton" aria-hidden="true">');
            for (var i = 0; i < 6; i++) lines.append('<span class="ns-skeleton"></span>');
            return lines;
        };

        var load = function (url) {
            if (request) request.abort();
            sheet.attr('aria-busy', 'true');
            request = $.ajax({ url: url, cache: false, dataType: 'html' })
                .done(function (html) { setContent($.parseHTML(html, document, false)); })
                .fail(function (xhr, status) {
                    if (status === 'abort') return;
                    setContent($('<div class="ns-error-state">').append(nightshift.icon('exclamation-circle-fill'))
                        .append($('<p>').text(nightshift.describeRequestError(xhr))));
                })
                .always(function () { request = null; sheet.removeAttr('aria-busy'); });
        };

        var isOpen = function () {
            return !!sheet && sheet.hasClass('show');
        };

        var setInert = function (inert) {
            $('#wrap').each(function () { this.inert = inert; });
        };

        var open = function (options) {
            options = options || {};
            if (!sheet) build();
            clearTimeout(hideTimer);

            var wasOpen = isOpen();
            var opener = options.opener || (wasOpen && state ? state.opener : document.activeElement);
            state = { opener: opener, onClose: options.onClose };

            sheet.find('#nsSheetTitle').text(options.title || '');
            sheet.find('#nsSheetOverline').text(options.overline || '').prop('hidden', !options.overline);
            sheet.attr('aria-describedby', options.overline ? 'nsSheetOverline' : null);
            sheet.toggleClass('ns-sheet-wide', !!options.wide);

            if (options.url) {
                setContent(skeleton());
                load(options.url);
            } else {
                setContent(options.content);
            }

            if (!wasOpen) {
                scrim.prop('hidden', false);
                sheet.prop('hidden', false);
                // The closed position is drawn first, so the sheet slides in
                void sheet[0].offsetWidth;
                scrim.addClass('show');
                sheet.addClass('show');
                setInert(true);
            }

            sheet.trigger('focus');
            return nightshift.sheet;
        };

        var close = function () {
            if (!isOpen()) return;
            var closing = state;
            state = null;
            if (request) request.abort();

            sheet.removeClass('show');
            scrim.removeClass('show');
            setInert(false);
            hideTimer = setTimeout(function () {
                sheet.prop('hidden', true);
                scrim.prop('hidden', true);
                nightshift.disposeTooltips(sheet.find('.ns-sheet-body, .ns-sheet-actions'));
            }, nightshift.prefersReducedMotion() ? 0 : 250);

            var opener = closing && closing.opener;
            if (opener && document.contains(opener)) focusBack(opener);
            else focusBack(document.getElementById('content'));
            if (closing && typeof closing.onClose === 'function') closing.onClose();
        };

        $(document).on('click', '.js-sheet-open', function (e) {
            e.preventDefault();
            var button = $(this);
            var options = {
                title: button.attr('data-sheet-title') || commandName(button),
                overline: button.attr('data-sheet-overline') || null,
                wide: button.attr('data-sheet-wide') === 'true',
                opener: this
            };

            if (button.attr('data-sheet-url')) {
                options.url = button.attr('data-sheet-url');
            } else {
                var selector = button.attr('data-sheet-content');
                var template = selector ? $(selector) : button.closest('tr, li, .ns-sheet-scope').find('template.js-sheet-template').first();
                if (!template.length) return;
                options.content = template[0].content ? document.importNode(template[0].content, true) : $(template.html());
            }

            open(options);
        });

        return { open: open, close: close, isOpen: isOpen };
    })();

    // Outlined text fields: NightShift.fields(container) links each .ns-field's label, supporting and error text to
    // its control (for, aria-describedby); NightShift.fieldError(control, message) shows or clears the error
    // (aria-invalid). Fields added later are wired when fields() runs again over them.
    var fieldId = 0;
    nightshift.fields = function (container) {
        $(container || document).find('.ns-field').each(function () {
            var field = $(this);
            var control = field.find('input, select, textarea').first();
            if (!control.length) return;
            if (!control.attr('id')) control.attr('id', 'nsField' + (++fieldId));
            field.children('label').attr('for', control.attr('id'));
            if (control.prop('required')) field.addClass('ns-field-required');

            var described = [];
            field.find('.ns-field-supporting, .ns-field-error').each(function () {
                if (!this.id) this.id = 'nsFieldText' + (++fieldId);
                described.push(this.id);
            });
            if (described.length) control.attr('aria-describedby', described.join(' '));
        });
    };

    nightshift.fieldError = function (control, message) {
        control = $(control);
        var field = control.closest('.ns-field');
        var error = field.find('.ns-field-error');
        if (!error.length && message) {
            error = $('<div class="ns-field-error">').appendTo(field);
            nightshift.fields(field.parent());
        }
        error.text(message || '');
        control.attr('aria-invalid', message ? 'true' : null);
        field.toggleClass('ns-field-invalid', !!message);
    };

    // What "Delete all" or "Requeue all" did: { processed, remaining, complete }
    nightshift.formatListResult = function (data) {
        var text = String(nightshift.config.text.listResult).replace('{0}', data.processed);
        if (data.complete === false) {
            text += ' ' + String(nightshift.config.text.listRemaining).replace('{0}', typeof data.remaining === 'number' ? data.remaining : '?');
        }
        return text;
    };

    nightshift.Metrics = (function() {
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

    // The x axis of the realtime graph, drawn as chartjs-plugin-streaming drew its realtime scale: ticks at whole
    // multiples of the smallest step that leaves room for every label (5 seconds on a wide screen), so they move
    // with the time instead of being chosen again on each frame, and labels cut at the edges of the plot, which
    // keeps its width while ticks come and go there.
    var RealtimeScale = typeof Chart === 'undefined' ? null : class extends Chart.TimeScale {
        buildTicks() {
            super.buildTicks();

            var step = this._tickStep();
            var ticks = [];
            for (var time = Math.ceil(this.min / step) * step; time <= this.max; time += step) {
                ticks.push({ value: time });
            }

            return ticks;
        }

        // Like Chart.js 2's time scale: the smallest step whose labels fit, with room for one more
        _tickStep() {
            var font = this.options.ticks.font || {};
            var ctx = this.ctx;

            ctx.save();
            ctx.font = [font.style, font.weight, (font.size || 12) + 'px', font.family || Chart.defaults.font.family].join(' ').trim();
            var labelWidth = ctx.measureText(this.format(this.max)).width || 1;
            ctx.restore();

            var capacity = Math.max(Math.floor(this.width / labelWidth) - 1, 1);
            var range = this.max - this.min;
            var steps = RealtimeScale.steps;

            for (var i = 0; i < steps.length; i++) {
                if (range / steps[i] <= capacity) return steps[i];
            }

            return steps[steps.length - 1];
        }

        fit() {
            super.fit();

            if (this.isHorizontal() && this.options.display && this.options.ticks.display) {
                this.paddingLeft = 3;
                this.paddingRight = 3;
            }
        }

        drawLabels(chartArea) {
            var ctx = this.ctx;

            ctx.save();
            ctx.beginPath();
            ctx.rect(chartArea.left, 0, chartArea.right - chartArea.left, this.chart.height);
            ctx.clip();
            super.drawLabels(chartArea);
            ctx.restore();
        }
    };

    if (RealtimeScale) {
        RealtimeScale.id = 'nightshiftRealtime';
        // No ticks from the data and no auto-skipping: buildTicks chooses them
        RealtimeScale.defaults = { ticks: { source: 'data', autoSkip: false } };
        RealtimeScale.steps = [1, 2, 5, 10, 15, 30, 60].map(function (seconds) { return seconds * 1000; });
        Chart.register(RealtimeScale);
    }

    // The realtime graph: the jobs that succeeded, failed or were deleted between two polls of the statistics, over
    // the last minute. It does what chartjs-plugin-streaming did with the settings the dashboard gave it (duration
    // 60 seconds, delay one poll interval, no ttl, 30 frames per second):
    // - the x axis shows the minute before now minus the delay, and slides with the clock: it is redrawn at most 30
    //   times a second from requestAnimationFrame, so nothing is drawn while the tab is hidden;
    // - each poll adds a point at the time it arrived, which comes in at the right edge one poll interval later;
    // - points that left the axis are dropped but the last two, so the areas still reach the left edge.
    nightshift.RealtimeGraph = (function () {
        var DURATION = 60 * 1000;
        var FRAME_INTERVAL = 1000 / 30;

        function RealtimeGraph(element, succeeded, failed, deleted, succeededStr, failedStr, deletedStr, pollInterval) {
            this._succeeded = succeeded;
            this._failed = failed;
            this._deleted = deleted;
            this._last = Date.now();
            this._pollInterval = pollInterval;
            this._duration = DURATION;
            this._delay = pollInterval;
            this._frameId = null;
            this._lastFrame = null;

            var range = this._range(Date.now());
            var options = graphOptions(
                {
                    type: 'nightshiftRealtime',
                    min: range.min,
                    max: range.max,
                    time: { unit: 'second', tooltipFormat: 'dateTimeWithSeconds', displayFormats: { second: 'timeWithSeconds', minute: 'timeWithSeconds' } }
                },
                { min: 0, suggestedMax: 10 });

            options.animation = false;

            this._chart = new Chart(element, {
                type: 'line',
                data: { datasets: graphDatasets({ failed: failedStr, deleted: deletedStr, succeeded: succeededStr }) },
                options: options
            });

            var self = this;
            this._onFrame = function (time) { self._frame(time); };
            this._paused = false;

            document.addEventListener('visibilitychange', function () {
                if (document.hidden) {
                    self._stop();
                } else {
                    self._start();
                }
            });

            // With reduced motion the axis doesn't slide: the graph steps once per poll
            if (reducedMotion.addEventListener) {
                reducedMotion.addEventListener('change', function () {
                    self._stop();
                    self._start();
                });
            }

            this._start();
        }

        // Pausing freezes the graph; the polls go on, so it shows the right minute when it runs again
        RealtimeGraph.prototype.setPaused = function (paused) {
            this._paused = !!paused;
            if (this._paused) this._stop(); else this._start();
        };

        RealtimeGraph.prototype._step = function () {
            if (this._paused || document.hidden) return;
            var now = Date.now();
            var range = this._range(now);
            var x = this._chart.options.scales.x;
            x.min = range.min;
            x.max = range.max;
            this._forgetAfterClockJump(now);
            this._dropOld(range.min);
            this._chart.update('none');
        };

        RealtimeGraph.prototype.appendHistory = function (statistics) {
            var newFailed = parseInt(statistics["failed:count"].intValue);
            var newDeleted = parseInt(statistics["deleted:count"].intValue);
            var newSucceeded = parseInt(statistics["succeeded:count"].intValue);
            var now = Date.now();

            if (this._succeeded !== null && this._failed !== null && this._deleted !== null && (now - this._last < this._pollInterval * 2)) {
                var failed = Math.max(newFailed - this._failed, 0);
                var deleted = Math.max(newDeleted - this._deleted, 0);
                var succeeded = Math.max(newSucceeded - this._succeeded, 0);

                // Points are kept in the order of their times, which the trimming relies on
                this._forgetAfterClockJump(now);

                this._chart.data.datasets[0].data.push({ x: now, y: failed });
                this._chart.data.datasets[1].data.push({ x: now, y: deleted });
                this._chart.data.datasets[2].data.push({ x: now, y: succeeded });

                // The next frame draws it; in a hidden tab, the points still mustn't pile up
                this._dropOld(this._range(now).min);

                // Without frames (reduced motion) each poll redraws the graph once
                if (nightshift.prefersReducedMotion()) this._step();
            }

            this._failed = newFailed;
            this._deleted = newDeleted;
            this._succeeded = newSucceeded;
            this._last = now;
        };

        RealtimeGraph.prototype.changeDatasetColorScheme = changeDatasetColorScheme;

        RealtimeGraph.prototype._range = function (now) {
            var max = now - this._delay;
            return { min: max - this._duration, max: max };
        };

        RealtimeGraph.prototype._start = function () {
            if (this._paused) return;
            if (nightshift.prefersReducedMotion()) {
                this._step();
                return;
            }

            if (this._frameId === null && !document.hidden) {
                this._lastFrame = null;
                this._frameId = window.requestAnimationFrame(this._onFrame);
            }
        };

        RealtimeGraph.prototype._stop = function () {
            if (this._frameId !== null) {
                window.cancelAnimationFrame(this._frameId);
                this._frameId = null;
            }
        };

        // time is requestAnimationFrame's timestamp: the frames are counted on that monotonic clock, so a wall clock
        // set back (or forward) doesn't stop them; only the axis follows Date.now()
        RealtimeGraph.prototype._frame = function (time) {
            this._frameId = window.requestAnimationFrame(this._onFrame);

            // A frame of a 60 Hz display comes up to a millisecond early
            if (this._lastFrame !== null && time - this._lastFrame < FRAME_INTERVAL - 2) return;
            this._lastFrame = time;

            var now = Date.now();
            var range = this._range(now);
            var x = this._chart.options.scales.x;
            x.min = range.min;
            x.max = range.max;

            this._forgetAfterClockJump(now);
            this._dropOld(range.min);
            // Without animations; an update replays the last mouse event, so an open tooltip follows its points
            this._chart.update('none');
        };

        // Every point has the time it arrived at, so a point later than now means the clock was set back, and a
        // newest point that left the axis means it jumped forward (or nothing came for a minute): the points are
        // dropped, and the graph starts again from the next poll. Points after now would also keep the ones before
        // them from being trimmed, which relies on their order.
        RealtimeGraph.prototype._forgetAfterClockJump = function (now) {
            var datasets = this._chart.data.datasets;
            var data = datasets[0].data;
            if (data.length === 0) return;

            var newest = data[data.length - 1].x;
            if (newest <= now && newest >= this._range(now).min) return;

            for (var i = 0; i < datasets.length; i++) {
                datasets[i].data = [];
            }
        };

        RealtimeGraph.prototype._dropOld = function (min) {
            var datasets = this._chart.data.datasets;

            for (var i = 0; i < datasets.length; i++) {
                var data = datasets[i].data;
                while (data.length > 2 && data[2].x <= min) {
                    data.shift();
                }
            }
        };

        return RealtimeGraph;
    })();

    nightshift.HistoryGraph = (function () {
        function HistoryGraph(element, succeeded, failed, deleted, succeededStr, failedStr, deletedStr) {
            var timeOptions = $(element).data('period') === 'week'
                ? { unit: 'day', tooltipFormat: 'longDate', displayFormats: { day: 'shortDate' } }
                : { unit: 'hour', tooltipFormat: 'dateTime', displayFormats: { hour: 'time', day: 'shortDate' } };

            this._chart = new Chart(element, {
                type: 'line',
                data: {
                    datasets: graphDatasets(
                        { failed: failedStr, deleted: deletedStr, succeeded: succeededStr },
                        { failed: failed, deleted: deleted, succeeded: succeeded })
                },
                options: graphOptions({ type: 'time', time: timeOptions }, {})
            });
        }

        HistoryGraph.prototype.changeDatasetColorScheme = changeDatasetColorScheme;

        return HistoryGraph;
    })();

    nightshift.StatisticsPoller = (function() {
        function StatisticsPoller(metricsCallback, statisticsUrl, pollInterval) {
            this._metricsCallback = metricsCallback;
            this._listeners = [];
            this._statisticsUrl = statisticsUrl;
            this._pollInterval = pollInterval;
            this._timeoutId = null;
        }

        // Polls the statistics. While the server doesn't answer, the status chip of the app bar says "Offline –
        // retrying" and the polls go on, further apart (up to a minute); the first answer puts everything back.
        StatisticsPoller.prototype.start = function () {
            var self = this;
            this._failures = 0;
            this._running = true;
            this._timeoutId = setTimeout(function () { self._tick(); }, this._pollInterval);
        };

        StatisticsPoller.prototype._tick = function () {
            var self = this;
            this.pollNow().always(function () {
                if (!self._running) return;
                var delay = self._failures === 0
                    ? self._pollInterval
                    : Math.min(self._pollInterval * Math.pow(2, Math.min(self._failures, 6)), 60 * 1000);
                self._timeoutId = setTimeout(function () { self._tick(); }, delay);
            });
        };

        // Polls once now; the promise is resolved when the server answered
        StatisticsPoller.prototype.pollNow = function () {
            var self = this;
            return $.post(this._statisticsUrl, { metrics: this._metricsCallback() })
                .done(function (data) {
                    self._failures = 0;
                    NightShift.Shell.setOnline(true);
                    try {
                        self._notifyListeners(data);
                    } catch (e) {
                        if (window.console && console.error) console.error(e);
                    }
                })
                .fail(function () {
                    self._failures++;
                    NightShift.Shell.setOnline(false);
                });
        };

        StatisticsPoller.prototype.stop = function() {
            this._running = false;
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

    nightshift.RecurringJobEditor = (function () {
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
            this._startAt = '';
            this._endAt = '';

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
                    return $(this).attr('data-id') === decodeURIComponent(match[1].replace(/\+/g, ' '));
                }).first();

                if (button.length) {
                    this.open(button);
                }
            }
        }

        RecurringJobEditor.prototype.open = function (button) {
            // .attr, not .data: jQuery's .data() converts the text, so the id "[7]" read as the array [7] and
            // the update was posted for the job "7"
            this._jobId = button.attr('data-id') || '';
            this._modal.find('.js-recurring-edit-id').text(this._jobId);
            this._cron.val(button.attr('data-cron') || '');
            // The list holds the host's own zones. A job's zone can be missing there (an IANA id on Windows, a
            // Windows id on Linux, an id only a custom resolver knows), so it's added rather than left unselected,
            // which previewed in UTC; the server resolves it like the scheduler does.
            var timezone = button.attr('data-timezone') || 'UTC';
            if (!this._timezone.find('option').filter(function () { return this.value === timezone; }).length) {
                this._timezone.append($('<option>').val(timezone).text(timezone));
            }
            this._timezone.val(timezone);
            this._queue.val(button.attr('data-queue') || '');
            this._misfire.val(button.attr('data-misfire') || 'Relaxed');
            this._startAt = button.data('start-at') || '';
            this._endAt = button.data('end-at') || '';
            this._error.hide().text('');
            this._preview.empty();
            this._description.text('');
            this._resolved.text('');
            var cron = this._cron;
            this._modal[0].addEventListener('shown.bs.modal', function () { cron.trigger('focus'); }, { once: true });
            bootstrap.Modal.getOrCreateInstance(this._modal[0]).show();
            this._refreshPreview();
        };

        RecurringJobEditor.prototype._schedulePreview = function () {
            var self = this;
            clearTimeout(this._previewTimer);
            this._previewTimer = setTimeout(function () { self._refreshPreview(); }, 300);
        };

        RecurringJobEditor.prototype._refreshPreview = function () {
            var self = this;
            var cron = trim(this._cron.val());

            if (this._previewRequest) {
                this._previewRequest.abort();
            }

            if (!cron) {
                this._preview.empty();
                this._description.text('');
                this._resolved.text('');
                return;
            }

            this._previewRequest = $.get(this._previewUrl, { cron: cron, timezone: this._timezone.val(), start: this._startAt, end: this._endAt })
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
                            .append($('<span>').text(NightShiftIntl.format(time, format)))
                            .append($('<span>').addClass('text-muted').text(NightShiftIntl.fromNow(time))));
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

            nightshift.setLoading(save, true);
            this._error.hide();

            $.post(this._updateUrl + nightshift.routeValue(this._jobId), {
                cron: trim(this._cron.val()),
                timezone: this._timezone.val(),
                // An empty queue is sent too: it puts the job back in the default queue
                queue: trim(this._queue.val()),
                misfire: this._misfire.val()
            })
                .done(function () { window.location.reload(); })
                .fail(function (xhr) {
                    nightshift.setLoading(save, false);
                    var message = xhr.responseJSON && xhr.responseJSON.error
                        ? xhr.responseJSON.error
                        : nightshift.describeRequestError(xhr);
                    self._showError(message);
                });
        };

        RecurringJobEditor.prototype._showError = function (message) {
            this._error.text(message).show();
        };

        return RecurringJobEditor;
    })();

    nightshift.LiveJobs = (function () {
        function LiveJobs(pollInterval) {
            this._pollInterval = Math.max(pollInterval || 2000, 1000);
            this._details = $('#jobLive');
            this._rows = $('.js-live-row[data-job-id]');
            this._url = this._details.data('live-url') || $('#nightshiftConfig').data('liveurl');
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
            var jobId = this._details.attr('data-job-id');
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
                bar.css('width', percent + '%').text(percent + '%').closest('.progress').attr('aria-valuenow', percent);
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
                var time = $('<span>').addClass('job-console-time').text(NightShiftIntl.format(entry.t, 'timeWithSeconds'));

                if (entry.c) {
                    lines.push($('<div>').addClass('job-console-line job-console-checkpoint').append(time)
                        .append(nightshift.icon('flag-fill')).append(document.createTextNode(' ' + entry.c)));

                    var delta = previous !== null ? '+' + LiveJobs.formatDuration(entry.t - previous) : NightShiftIntl.format(entry.t, 'timeWithSeconds');
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

        // The server answers for at most this many jobs per request
        LiveJobs.MAX_JOBS = 100;

        LiveJobs.prototype._startRows = function () {
            var self = this;
            var ids = this._rows.map(function () { return $(this).attr('data-job-id'); }).get().slice(0, LiveJobs.MAX_JOBS);

            var poll = function () {
                // A hidden tab doesn't need the rows; the next poll after it's shown again catches up
                if (document.hidden) {
                    self._timer = setTimeout(poll, self._pollInterval);
                    return;
                }

                // The rows show the state and the progress only, so the server skips reading the consoles
                $.post(self._url, { 'jobs[]': ids, console: 0 })
                    .done(function (data) {
                        self._rows.each(function () {
                            var row = $(this);
                            var job = data.jobs && data.jobs[row.attr('data-job-id')];
                            if (!job) return;

                            if (job.progress) {
                                row.find('.js-live-bar').css('width', job.progress.p + '%').closest('.progress').attr('aria-valuenow', job.progress.p);
                                row.find('.js-live-progress-message').text(job.progress.p + '% ' + (job.progress.m || ''));
                                nightshift.setTitle(row.find('.progress').removeClass('job-live-progress-eta job-live-progress-late'), job.progress.m || '');
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

    nightshift.Page = (function() {
        function Page(config) {
            // The server's HTML of each row of a list, before the scripts change it, for the live refresh to compare
            $('.js-jobs-list table > tbody > tr').each(function () { $(this).data('ns-html', this.outerHTML); });

            this._metrics = new NightShift.Metrics();

            var self = this;
            this._poller = new NightShift.StatisticsPoller(
                function () { return self._metrics.getNames(); },
                config.pollUrl,
                config.pollInterval);

            this._initialize(config.locale);

            this.realtimeGraph = this._createRealtimeGraph('realtimeGraph', config.pollInterval);
            this.historyGraph = this._createHistoryGraph('historyGraph');

            var editorElement = document.getElementById('recurringJobEditor');
            this.recurringJobEditor = editorElement ? new NightShift.RecurringJobEditor(editorElement) : null;
            this.theme = new NightShift.Theme();
            this.shell = new NightShift.Shell();
            this.dialogs = new NightShift.Dialogs();
            nightshift.markFittingTables();
            var fitTimer = null;
            $(window).on('resize', function () { clearTimeout(fitTimer); fitTimer = setTimeout(nightshift.markFittingTables, 150); });
            nightshift.fields();
            nightshift.snackbar.restore();
            this.liveJobs = new NightShift.LiveJobs(config.pollInterval);
            this.commandPalette = new NightShift.CommandPalette(config);
            this.liveTable = new NightShift.LiveTable(this, config);
            this.activityFeed = new NightShift.ActivityFeed(config);
            this.catalogRunner = document.getElementById('catalogRunner') ? new NightShift.CatalogRunner(document.getElementById('catalogRunner')) : null;
            this.chainFlows = $('.js-flow').map(function () { return NightShift.ChainFlow.create(this); }).get();
            this.operations = new NightShift.Operations();
            this.watchlist = new NightShift.Watchlist(config);
            this.shortcuts = new NightShift.Shortcuts(config);

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
                var realtimeGraph = new NightShift.RealtimeGraph(realtimeElement, succeeded, failed, deleted, succeededStr, failedStr, deletedStr, pollInterval);

                this._poller.addListener(function (data) {
                    realtimeGraph.appendHistory(data);
                });

                // The card's "Pause graph" switch
                $(realtimeElement).closest('.hf-card').find('.js-graph-pause').on('change', function () {
                    realtimeGraph.setPaused(this.checked);
                    $(this).closest('.hf-card').find('.js-graph-dot').toggleClass('paused', this.checked);
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

                return new NightShift.HistoryGraph(historyElement, succeeded, failed, deleted, succeededStr, failedStr, deletedStr);
            }

            return null;
        };

        Page.prototype.updateRelativeDates = function () {
            this._updateRelativeDates();
        };

        Page.prototype._initialize = function (locale) {
            NightShiftIntl.setLocale(locale);
            var self = this;
            var updateRelativeDates = function () {
                $('*[data-moment]').each(function () {
                    var $this = $(this);
                    var timestamp = $this.data('moment');

                    if (timestamp) {
                        var time = timestamp * 1000;
                        $this.html(NightShiftIntl.fromNow(time)).attr('data-bs-container', 'body');
                        nightshift.setTitle($this, NightShiftIntl.format(time, 'fullDateTime'));
                    }
                });

                $('*[data-moment-title]').each(function () {
                    var $this = $(this);
                    var timestamp = $this.data('moment-title');

                    if (timestamp) {
                        var time = timestamp * 1000;
                        nightshift.setTitle($this.attr('data-bs-container', 'body'), NightShiftIntl.format(time, 'fullDateTime'));
                    }
                });

                $('*[data-moment-local]').each(function () {
                    var $this = $(this);
                    var timestamp = $this.data('moment-local');

                    if (timestamp) {
                        var time = timestamp * 1000;
                        $this.html(NightShiftIntl.format(time, $this.data('moment-local-format') || 'numericDateTime'));
                    }
                });
            };

            this._updateRelativeDates = updateRelativeDates;
            updateRelativeDates();
            setInterval(updateRelativeDates, 30 * 1000);

            nightshift.tooltips();

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

            // Every request to the dashboard says it comes from the dashboard's scripts: without antiforgery, the
            // server only accepts POSTs that carry this header, which a form on another site can't send. Requests to
            // other origins (a dashboard extension calling another service) get neither the header, which would
            // need a CORS preflight, nor the token, which must not leave the dashboard.
            var headers = { 'X-Requested-With': 'XMLHttpRequest' };

            if (csrfToken && csrfHeader) {
                headers[csrfHeader] = csrfToken;
            }

            $.ajaxPrefilter(function (options, originalOptions, xhr) {
                if (options.crossDomain) return;

                $.each(headers, function (name, value) {
                    xhr.setRequestHeader(name, value);
                });
            });

            // A form posted in the background, then the page reloads or goes to data-redirect
            $(document).on('submit', '.js-ajax-form', function (e) {
                var $form = $(this);
                var $submit = $form.find('[type=submit]');
                var name = commandName($submit.first());
                e.preventDefault();

                nightshift.setLoading($submit, true);
                $.post($form.attr('action'), $form.serialize())
                    .done(function () {
                        if (name) nightshift.snackbar.showAfterReload({ text: format(TEXT.done, name) });
                        var target = $form.data('redirect');
                        if (target) {
                            window.location.href = target;
                        } else {
                            window.location.reload();
                        }
                    })
                    .fail(function (xhr) {
                        nightshift.setLoading($submit, false);
                        nightshift.showRequestError(xhr, name, function () { $form.trigger('submit'); });
                    });
            });

            // The rows-per-page menu of a list's pagination footer goes to the page of that size
            $(document).on('change', '.js-per-page', function () {
                var url = $(this).val();
                if (url && url !== '#') window.location.href = url;
            });

            // A row with data-href opens it when clicked anywhere, except on its own links, buttons and fields,
            // or when the click ends a text selection; Ctrl, Cmd, Shift or the middle button open a new tab
            $(document).on('click auxclick', 'tr[data-href]', function (e) {
                if (e.type === 'auxclick' && e.which !== 2) return;
                if ($(e.target).closest('a, button, input, select, textarea, label, [data-ajax]').length > 0) return;
                var selection = window.getSelection ? window.getSelection().toString() : '';
                if (selection.length > 0) return;

                var href = $(this).data('href');
                if (!href) return;

                if (e.type === 'auxclick' || e.ctrlKey || e.metaKey || e.shiftKey) {
                    window.open(href, '_blank', 'noopener');
                } else {
                    window.location.href = href;
                }
            });

            // A command button: asks in the confirmation dialog when it has data-confirm, posts, and says what
            // happened in a snackbar (after the reload that shows the result), or why it failed, with Retry
            $(document).on('click', '*[data-ajax]', function (e) {
                var $this = $(this);
                e.preventDefault();

                nightshift.confirmButton($this).then(function (confirmed) {
                    if (!confirmed) return;
                    var name = commandName($this);
                    var run = function () {
                        nightshift.setLoading($this, true);

                        $.post($this.attr('data-ajax'))
                            .done(function (data) {
                                // "Delete all" and "Requeue all" say how much they did before the list reloads
                                if (data && typeof data.processed === 'number') {
                                    nightshift.snackbar.show({ text: nightshift.formatListResult(data), kind: data.complete === false ? 'warning' : null });
                                    setTimeout(function () { window.location.reload(); }, data.complete === false ? 5000 : 1500);
                                    return;
                                }

                                if (name) nightshift.snackbar.showAfterReload({ text: format(TEXT.done, name) });
                                window.location.reload();
                            })
                            .fail(function (xhr) {
                                nightshift.setLoading($this, false);
                                nightshift.showRequestError(xhr, name, run);
                            });
                    };

                    run();
                });
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
                        $(row).toggleClass('highlight', isSelected).attr('aria-selected', isSelected ? 'true' : 'false');
                    }
                };

                // The contextual selection bar: "N selected", the list's own bulk commands and Clear. Its buttons
                // click the list's, so they ask and post as those do. It is built again with the list's toolbar.
                var commands = $('.js-jobs-list-command', container);
                $(container).children('.ns-selection-bar').remove();
                var bar = $();
                if (commands.length) {
                    bar = $('<div class="ns-selection-bar" hidden>')
                        .append($('<button type="button" class="ns-icon-button js-selection-clear">').attr({ 'aria-label': TEXT.clearSelection, title: TEXT.clearSelection }).html(nightshift.icon('x-lg')))
                        .append('<span class="ns-selection-count" aria-live="polite"></span>');

                    commands.each(function () {
                        var command = $(this);
                        $('<button type="button" class="btn btn-sm">')
                            .addClass(command.is('.btn-death, .btn-danger') || /\/(delete|remove)$/.test(command.attr('data-url') || '') ? 'ns-btn-text ns-selection-danger' : 'ns-btn-text')
                            .html(command.html())
                            .on('click', function () { command.trigger('click'); })
                            .appendTo(bar);
                    });

                    var anchor = $(container).find('.ns-table-card, .table-responsive, table').first();
                    if (anchor.length) bar.insertBefore(anchor); else bar.prependTo(container);
                }

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

                    var count = $('.js-jobs-list-checkbox:checked', container).length;
                    if (bar.length) {
                        bar.prop('hidden', count === 0);
                        bar.find('.ns-selection-count').text(format(TEXT.selectedCount, count));
                    }
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

                $(container).off('click.nightshift');

                $(container).on('click.nightshift', '.js-jobs-list-checkbox', function(e) {
                    selectRow(
                        $(this).closest('.js-jobs-list-row').first(),
                        $(this).is(':checked'));

                    updateListState();

                    e.stopPropagation();
                });

                $(container).on('click.nightshift', '.js-jobs-list-row', function (e) {
                    if ($(e.target).is('a') || $(e.target).closest('button').length) return;

                    toggleRowSelection(this);
                    updateListState();
                });

                $(container).on('click.nightshift', '.js-jobs-list-select-all', function() {
                    var selectRows = $(this).is(':checked');

                    $('.js-jobs-list-row', container).each(function() {
                        selectRow(this, selectRows);
                    });

                    updateListState();
                });

                $(container).on('click.nightshift', '.js-selection-clear', function () {
                    $('.js-jobs-list-row', container).each(function () { selectRow(this, false); });
                    updateListState();
                    var first = $('.js-jobs-list-select-all', container)[0] || $('.js-jobs-list-checkbox', container)[0];
                    if (first) first.focus();
                });

                $(container).on('click.nightshift', '.js-jobs-list-command', function(e) {
                    var $this = $(this);
                    e.preventDefault();

                    var jobs = $("input[name='jobs[]']:checked", container).map(function() {
                        return $(this).val();
                    }).get();

                    nightshift.confirmButton($this, jobs.length).then(function (confirmed) {
                        if (!confirmed) return;
                        var name = commandName($this);
                        var run = function () {
                            nightshift.setLoading($this, true);

                            $.post($this.attr('data-url'), { 'jobs[]': jobs })
                                .done(function () {
                                    if (name) nightshift.snackbar.showAfterReload({ text: format(TEXT.done, name) });
                                    window.location.reload();
                                })
                                .fail(function (xhr) {
                                    nightshift.setLoading($this, false);
                                    nightshift.showRequestError(xhr, name, run);
                                });
                        };

                        run();
                    });
                });

                updateListState();
            })();
        };

        return Page;
    })();

    nightshift.CommandPalette = (function () {
        var DEFAULT_PAGES = [
            { name: 'Overview', path: '/' },
            { name: 'Enqueued jobs', path: '/jobs/enqueued' },
            { name: 'Scheduled jobs', path: '/jobs/scheduled' },
            { name: 'Processing jobs', path: '/jobs/processing' },
            { name: 'Succeeded jobs', path: '/jobs/succeeded' },
            { name: 'Failed jobs', path: '/jobs/failed' },
            { name: 'Deleted jobs', path: '/jobs/deleted' },
            { name: 'Awaiting jobs', path: '/jobs/awaiting' },
            { name: 'Batches', path: '/batches' },
            { name: 'Retries', path: '/retries' },
            { name: 'Recurring jobs', path: '/recurring' },
            { name: 'Audit', path: '/audit' },
            { name: 'Insights', path: '/insights' },
            { name: 'Catalog', path: '/catalog' },
            { name: 'Tags', path: '/tags' },
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
            var pages = [];
            var seen = {};

            // The navigation first: its names are in the dashboard's language, and it holds the pages of extensions
            $('.hf-nav a[href], .navbar-nav a[href], .list-group a[href]').each(function () {
                var link = $(this);
                var url = link.attr('href');
                var label = link.find('.hf-nav-text');
                var name = trim(label.length ? label.text() : link.clone().children().remove().end().text());
                if (url && name && !seen[url]) {
                    seen[url] = true;
                    pages.push({ name: name, url: url });
                }
            });

            DEFAULT_PAGES.forEach(function (page) {
                var url = self._basePath + page.path;
                if (!seen[url]) {
                    seen[url] = true;
                    pages.push({ name: page.name, url: url });
                }
            });

            return pages;
        };

        CommandPalette.prototype._build = function () {
            var placeholder = TEXT.palettePlaceholder;
            var kbd = function (key) { return $('<kbd>').text(key)[0].outerHTML; };
            var hint = $('<div class="hf-palette-hint">')
                .append($('<span>').html(format(escapeAttribute(TEXT.paletteHint), kbd('↑') + kbd('↓'), kbd('Enter'), kbd('Esc'), kbd('>'))))
                .append($('<span class="hf-palette-hint-right">').html(format(escapeAttribute(TEXT.paletteHintAnywhere), kbd('Ctrl K'), kbd('/'))));

            // An MD3 search view holding a combobox: the input keeps the focus, and aria-activedescendant names the
            // option the arrow keys select, so screen readers follow the selection without moving the focus
            this._overlay = $('<div class="hf-palette" role="dialog" aria-modal="true" style="display: none">').attr('aria-label', TEXT.paletteLabel)
                .append($('<div class="hf-palette-box">')
                    .append($('<div class="hf-palette-bar">')
                        .append(nightshift.icon('search'))
                        .append(this._input = $('<input type="text" class="hf-palette-input" role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="hfPaletteResults" autocomplete="off" spellcheck="false">')
                            .attr({ placeholder: placeholder, 'aria-label': placeholder }))
                        .append(this._close = $('<button type="button" class="ns-icon-button hf-palette-close">').attr('aria-label', TEXT.close).html(nightshift.icon('x-lg'))))
                    .append(this._list = $('<ul class="hf-palette-results" id="hfPaletteResults" role="listbox">').attr('aria-label', TEXT.paletteResults))
                    .append(this._status = $('<div class="visually-hidden" role="status" aria-live="polite">'))
                    .append(hint));

            $('body').append(this._overlay);

            var self = this;

            this._close.on('click', function () { self.close(); });
            this._overlay.on('keydown', function (e) {
                if (e.key === 'Escape') { self.close(); e.preventDefault(); return; }
                trapTab(e, self._overlay[0]);
            });

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
            });

            // Only entries open something: the group headers and "Nothing found" are list items too, so the
            // position among the <li>s isn't the entry's index
            this._list.on('click', 'li.hf-palette-item', function () {
                var index = $(this).data('index');
                if (typeof index !== 'number') return;
                self._selected = index;
                self._open();
            });

            $(document).on('click', '.js-palette-open', function (e) {
                e.preventDefault();
                self.open();
            });
        };

        CommandPalette.prototype._bindKeys = function () {
            var self = this;

            $(document).on('keydown', function (e) {
                var pendingG = CommandPalette.pendingG;
                var inField = $(e.target).is('input, textarea, select, [contenteditable]');

                if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
                    e.preventDefault();
                    self.toggle();
                    return;
                }

                if (inField) return;

                // Ctrl+K above always works; the single-key shortcuts below can be turned off (WCAG 2.1.4)
                if (!nightshift.singleKeyShortcuts.enabled()) return;

                if (e.key === '/') {
                    e.preventDefault();
                    self.open();
                    return;
                }

                if (e.key === '?') {
                    if (window.NightShift.page && window.NightShift.page.shortcuts) window.NightShift.page.shortcuts.toggle();
                    return;
                }

                // "g" then a letter: go to a page
                if (e.key === 'g' && !e.ctrlKey && !e.metaKey && !e.altKey) {
                    CommandPalette.pendingG = Date.now();
                    e.stopImmediatePropagation();
                    return;
                }

                if (CommandPalette.isGoPending(pendingG) && !/^(Shift|Control|Alt|Meta|CapsLock)$/.test(e.key)) {
                    var targets = { j: '/jobs/enqueued', p: '/jobs/processing', f: '/jobs/failed', r: '/recurring', s: '/servers', i: '/insights', c: '/catalog', t: '/tags', b: '/batches', a: '/audit', h: '/' };
                    CommandPalette.pendingG = null;

                    // The second key belongs to "g": the list shortcuts ("r" requeues, "d" deletes) must not see it
                    e.stopImmediatePropagation();
                    e.preventDefault();

                    if (targets[e.key]) {
                        window.location.href = self._basePath + targets[e.key];
                    }
                }
            });
        };

        // When "g" was pressed, waiting for the letter of a page; shared with the list shortcuts
        CommandPalette.pendingG = null;

        CommandPalette.isGoPending = function (pendingG) {
            var since = pendingG === undefined ? CommandPalette.pendingG : pendingG;
            return !!since && Date.now() - since < 1500;
        };

        CommandPalette.prototype.open = function (query) {
            // Focus goes back where it was when the palette closes
            if (!this._overlay.is(':visible')) this._returnFocus = document.activeElement;
            this._overlay.show();
            if (query !== undefined) this._input.val(query);
            this._input.trigger('focus').trigger('select');
            this._search();
        };

        CommandPalette.prototype.close = function () {
            this._overlay.hide();
            if (this._request) this._request.abort();

            var target = this._returnFocus;
            this._returnFocus = null;
            if (target && target !== document.body && document.contains(target) && typeof target.focus === 'function') target.focus();
        };

        CommandPalette.prototype.toggle = function () {
            if (this._overlay.is(':visible')) this.close(); else this.open();
        };

        CommandPalette.prototype._commands = function (text) {
            var self = this;
            var base = this._basePath;
            var post = function (path, title) {
                var run = function () {
                    $.post(base + path)
                        .done(function () {
                            nightshift.snackbar.showAfterReload({ text: format(TEXT.done, title.replace(/^>\s*/, '')) });
                            window.location.reload();
                        })
                        .fail(function (xhr) { nightshift.showRequestError(xhr, title.replace(/^>\s*/, ''), run); });
                };
                return run;
            };
            // A read-only dashboard lists only the commands that change nothing
            var readOnly = nightshift.config.readOnly;
            var all = readOnly ? [] : [
                { title: '> pause processing', detail: 'hold every new job until resumed', run: post('/maintenance/pause', '> pause processing') },
                { title: '> resume processing', detail: 'let held jobs run again', run: post('/maintenance/resume', '> resume processing') }
            ];
            all = all.concat([
                { title: '> wallboard', detail: 'open the full-screen status board', run: function () { window.location.href = base + '/wallboard'; } },
                { title: '> timeline', detail: 'what every worker did recently', run: function () { window.location.href = base + '/timeline'; } },
                { title: '> keyboard shortcuts', detail: 'list the shortcuts', run: function () { if (window.NightShift.page && window.NightShift.page.shortcuts) window.NightShift.page.shortcuts.toggle(); } },
                nightshift.singleKeyShortcuts.enabled()
                    ? { title: '> turn single-key shortcuts off', detail: 'j, k, x, r, d, g, / and ? stop doing anything (Ctrl+K still works)', run: function () { nightshift.singleKeyShortcuts.set(false); } }
                    : { title: '> turn single-key shortcuts on', detail: 'j, k, x, r, d, g, / and ? work again', run: function () { nightshift.singleKeyShortcuts.set(true); } }
            ]);
            var match = readOnly ? null : /^>\s*(pause|resume)\s+(queue|server)\s+(\S+)/i.exec(text);
            if (match) {
                var kind = match[2].toLowerCase() === 'queue' ? '/queues/' : '/servers/';
                all.unshift({ title: '> ' + match[1].toLowerCase() + ' ' + match[2].toLowerCase() + ' ' + match[3], detail: 'post ' + kind + match[1].toLowerCase() + '/' + match[3], run: post(kind + match[1].toLowerCase() + '/' + nightshift.routeValue(match[3]), '> ' + match[1].toLowerCase() + ' ' + match[2].toLowerCase() + ' ' + match[3]) });
            }
            var job = readOnly ? null : /^>\s*(requeue|delete)\s+#?(\S+)/i.exec(text);
            if (job) {
                var verb = job[1].toLowerCase();
                all.unshift({ title: '> ' + verb + ' #' + job[2], detail: verb + ' job ' + job[2], run: post('/jobs/actions/' + verb + '/' + nightshift.routeValue(job[2]), '> ' + verb + ' #' + job[2]) });
            }
            var needle = text.replace(/^>\s*/, '').toLowerCase();
            return all.filter(function (c) { return !needle || c.title.toLowerCase().indexOf(needle) >= 0; })
                .map(function (c) { return { group: TEXT.paletteCommands, title: c.title, detail: c.detail, run: c.run }; });
        };

        CommandPalette.prototype._search = function () {
            var self = this;
            var query = trim(this._input.val());
            var lower = query.toLowerCase();

            if (query.charAt(0) === '>') {
                if (this._request) this._request.abort();
                this._render(this._commands(query));
                return;
            }

            var results = this._pages
                .filter(function (page) { return !lower || page.name.toLowerCase().indexOf(lower) >= 0; })
                .slice(0, lower ? 6 : 12)
                .map(function (page) { return { group: TEXT.palettePages, title: page.name, url: page.url }; });

            this._render(results);

            if (this._request) this._request.abort();
            if (!query) return;

            this._request = $.get(this._searchUrl, { q: query })
                .done(function (data) {
                    (data.jobs || []).forEach(function (job) {
                        results.push({ group: TEXT.paletteJobs, title: '#' + job.id + (job.name ? ' — ' + job.name : ''), detail: job.state, url: job.url });
                    });
                    (data.recurringJobs || []).forEach(function (job) {
                        results.push({ group: TEXT.paletteRecurring, title: job.id + (job.name ? ' — ' + job.name : ''), detail: (job.paused ? 'paused · ' : '') + (job.cron || ''), url: job.url });
                    });
                    (data.tags || []).forEach(function (tag) {
                        results.push({ group: TEXT.paletteTags, title: tag.tag, detail: tag.count + (tag.count === 1 ? ' job' : ' jobs'), url: tag.url });
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
                    this._list.append($('<li class="hf-palette-group" role="presentation">').text(group));
                }

                var item = $('<li class="hf-palette-item" role="option">').attr({ id: 'hfPaletteItem' + i, 'aria-selected': i === this._selected ? 'true' : 'false' })
                    .toggleClass('selected', i === this._selected)
                    .append($('<span class="hf-palette-title">').text(result.title));

                if (result.detail) {
                    item.append($('<span class="hf-palette-detail">').text(result.detail));
                }

                item.data('index', i);
                this._list.append(item);
            }

            if (results.length === 0) {
                this._list.append($('<li class="hf-palette-empty" role="presentation">').text(TEXT.paletteNothing));
            }

            this._updateActive();
            this._status.text(results.length === 0 ? TEXT.paletteNothing : results.length === 1 ? TEXT.paletteCountOne : format(TEXT.paletteCount, results.length));
        };

        CommandPalette.prototype._updateActive = function () {
            var hasSelection = this._results.length > 0;
            if (hasSelection) this._input.attr('aria-activedescendant', 'hfPaletteItem' + this._selected);
            else this._input.removeAttr('aria-activedescendant');
        };

        CommandPalette.prototype._move = function (delta) {
            if (this._results.length === 0) return;
            this._selected = (this._selected + delta + this._results.length) % this._results.length;
            this._list.find('.hf-palette-item').removeClass('selected').attr('aria-selected', 'false')
                .eq(this._selected).addClass('selected').attr('aria-selected', 'true')[0].scrollIntoView({ block: 'nearest' });
            this._updateActive();
        };

        CommandPalette.prototype._open = function () {
            var result = this._results[this._selected];
            if (!result) return;
            if (result.run) { this.close(); result.run(); return; }
            window.location.href = result.url;
        };

        return CommandPalette;
    })();

    nightshift.LiveTable = (function () {
        // Kept per list page: turning Live on for Failed jobs doesn't turn it on everywhere
        var STORAGE_PREFIX = 'nightshift.liveTable:';
        var OLD_STORAGE_KEY = 'nightshift.liveTable';
        // Asks the page for its content without the layout, and lets it skip what isn't in the list
        var LIST_REFRESH_QUERY = 'list-refresh=1';

        function LiveTable(page, config) {
            this._page = page;
            this._config = config;
            this._interval = Math.max((config.pollInterval || 2000) * 2, 2000);
            this._container = $('.js-jobs-list').first();
            this._basePath = (config.pollUrl || '').replace(/\/stats$/, '');
            this._storageKey = STORAGE_PREFIX + window.location.pathname;

            if (!this._container.length) return;

            try { window.localStorage.removeItem(OLD_STORAGE_KEY); } catch (e) { /* private mode */ }

            // The rows a refresh brings get their tooltips from this one, delegated, when they are first hovered or
            // focused. Bootstrap leaves a small entry in its event registry for every element that ever had a
            // tooltip, even after dispose, so creating one for every row on every refresh made a page left on Live
            // grow all day.
            if (window.bootstrap && bootstrap.Tooltip) {
                bootstrap.Tooltip.getOrCreateInstance(this._container[0], { selector: '[title], [data-bs-original-title]' });
            }

            this._enabled = this._read();
            this._build();
            this._place();
            if (this._enabled) this._schedule();
        }

        LiveTable.prototype._read = function () {
            try { return window.localStorage.getItem(this._storageKey) === 'true'; } catch (e) { return false; }
        };

        LiveTable.prototype._write = function (value) {
            try {
                if (value) window.localStorage.setItem(this._storageKey, 'true');
                else window.localStorage.removeItem(this._storageKey);
            } catch (e) { /* private mode */ }
        };

        LiveTable.listUrl = function (href) {
            var url = String(href).split('#')[0];
            return url + (url.indexOf('?') >= 0 ? '&' : '?') + LIST_REFRESH_QUERY;
        };

        // The buttons are built even when the list has no toolbar (a list of an extension's page may have none): Live
        // may be on for the page, and they go in the toolbar of the first refresh that brings one
        LiveTable.prototype._build = function () {
            var self = this;
            var config = this._config;
            var text = (config && config.text) || { refresh: 'Refresh', live: 'Live', liveHint: 'Refresh this list automatically' };

            this._refreshButton = $('<button type="button" class="btn btn-sm btn-outline-secondary hf-refresh-button">').attr('title', text.refresh)
                .append(nightshift.icon('arrow-clockwise')).append(document.createTextNode(' ' + text.refresh))
                .on('click', function () {
                    self._refreshButton.addClass('spinning');
                    self._refresh(true);
                });

            this._button = $('<button type="button" class="btn btn-sm btn-outline-secondary hf-live-toggle">').attr('title', text.liveHint)
                .append('<span class="hf-live-dot"></span>').append(document.createTextNode(' ' + text.live))
                .toggleClass('active', this._enabled)
                .attr('aria-pressed', this._enabled ? 'true' : 'false')
                .on('click', function () {
                    self._enabled = !self._enabled;
                    self._write(self._enabled);
                    self._button.toggleClass('active', self._enabled).attr('aria-pressed', self._enabled ? 'true' : 'false');
                    if (self._enabled) self._schedule(); else clearTimeout(self._timer);
                });
        };

        // Puts the buttons at the end of the list's toolbar, when it has one
        LiveTable.prototype._place = function () {
            this._container.find('.btn-toolbar-top').first().append(this._refreshButton).append(this._button);
        };

        LiveTable.prototype._schedule = function () {
            var self = this;
            clearTimeout(this._timer);
            this._timer = setTimeout(function () { self._refresh(); }, this._interval);
        };

        LiveTable.prototype._refresh = function (manual) {
            var self = this;

            // Never pull the rug from under a selection or an open dialog, unless asked by hand; and don't load a
            // tab nobody looks at
            if (!manual && (document.hidden || this._container.find('.js-jobs-list-checkbox:checked').length || $('.modal.show').length || nightshift.sheet.isOpen())) {
                this._schedule();
                return;
            }

            // A request that takes a while shows the bar along the top of the table
            var busy = setTimeout(function () { self._container.addClass('ns-list-busy'); }, 250);

            $.ajax({ url: LiveTable.listUrl(window.location.href), cache: false })
                .done(function (html) {
                    var nodes = $($.parseHTML(html, document, false));
                    var fresh = nodes.filter('.js-jobs-list').add(nodes.find('.js-jobs-list')).first();
                    self._updateCount(nodes);

                    if (fresh.length) {
                        var added = self._patch(fresh);
                        if (added === null) self._replace(fresh);

                        self._page.initJobsList(self._container[0]);
                        self._page.updateRelativeDates();
                        nightshift.tooltipRoles(self._container);
                        nightshift.markFittingTables(self._container);
                        self._button.addClass('hf-live-flash');
                        setTimeout(function () { self._button.removeClass('hf-live-flash'); }, 600);

                        if (added === null || added > 0 || self._changed) {
                            nightshift.announce(added > 0 ? format(TEXT.listUpdatedNew, added) : TEXT.listUpdated);
                        }
                    }
                })
                .always(function () {
                    clearTimeout(busy);
                    self._container.removeClass('ns-list-busy');
                    self._refreshButton.removeClass('spinning');
                    if (self._enabled) self._schedule();
                });
        };

        // The count chip of the page's header (Html.PageTitle) takes the number of the fresh page
        LiveTable.prototype._updateCount = function (nodes) {
            var fresh = nodes.filter('.js-list-count').add(nodes.find('.js-list-count')).first();
            var chip = $('.js-list-count').first();
            if (!fresh.length || !chip.length || chip.text() === fresh.text()) return;
            chip.text(fresh.text()).removeClass('ns-count-changed');
            void chip[0].offsetWidth;
            chip.addClass('ns-count-changed');
        };

        // The key of a row: its data-job-id, else the job its checkbox or its link names
        LiveTable.rowKey = function (row) {
            row = $(row);
            var key = row.attr('data-job-id') || row.find('.js-jobs-list-checkbox').attr('value');
            if (!key) {
                var link = row.find('a[href*="/jobs/details/"], a[href*="/recurring/details/"]').first().attr('href');
                if (link) key = link;
            }
            return key || null;
        };

        // Puts the fresh rows in place of the old ones by their keys, so the page keeps its scroll position, the
        // focus stays on the same row, and only changed rows are redrawn. The rest of the list (toolbar,
        // pagination) is replaced. Returns the number of new rows, or null when the rows have no keys or the table
        // changed shape, and the caller replaces the whole list instead.
        LiveTable.prototype._patch = function (fresh) {
            var self = this;
            var oldBody = this._container.find('table > tbody').first();
            var newBody = fresh.find('table > tbody').first();
            var oldHead = this._container.find('table > thead').first();
            var newHead = fresh.find('table > thead').first();
            this._changed = false;

            if (!oldBody.length || !newBody.length || oldHead.text() !== newHead.text()) return null;

            var oldRows = {};
            var keyless = false;
            oldBody.children('tr').each(function () {
                var key = LiveTable.rowKey(this);
                if (!key) { keyless = true; return; }
                oldRows[key] = this;
            });
            newBody.children('tr').each(function () { if (!LiveTable.rowKey(this)) keyless = true; });
            if (keyless) return null;

            // Where the focus is: the row and the position of the focused control in it
            var active = document.activeElement;
            var focusRow = active ? $(active).closest('tr')[0] : null;
            var focusKey = focusRow && $.contains(oldBody[0], focusRow) ? LiveTable.rowKey(focusRow) : null;
            var focusIndex = focusKey ? tabbables(focusRow).index(active) : -1;

            var added = 0;
            var kept = {};
            var previous = null;

            newBody.children('tr').each(function () {
                var key = LiveTable.rowKey(this);
                var old = oldRows[key];
                var row = this;
                // The server's HTML of the row, before the page's scripts changed it (relative times, selection)
                $(row).data('ns-html', row.outerHTML);

                if (old) {
                    kept[key] = true;
                    if ($(old).data('ns-html') === row.outerHTML) {
                        row = old;
                    } else {
                        nightshift.disposeTooltips(old);
                        $(old).replaceWith(row);
                        $(row).addClass('ns-row-updated');
                        self._changed = true;
                    }
                } else {
                    added++;
                    $(row).addClass('ns-row-updated');
                    self._changed = true;
                }

                if (previous) $(row).insertAfter(previous); else oldBody.prepend(row);
                previous = row;

                if (key === focusKey && row !== focusRow) {
                    var target = tabbables(row).eq(Math.max(focusIndex, 0));
                    if (target.length) target.trigger('focus');
                }
            });

            $.each(oldRows, function (key, row) {
                if (!kept[key]) {
                    nightshift.disposeTooltips(row);
                    $(row).remove();
                    self._changed = true;
                }
            });

            // The toolbar and the pagination: the Live buttons stay, with their tooltips
            this._button.detach();
            this._refreshButton.detach().removeClass('spinning');
            ['.btn-toolbar-top', '.ns-pagination'].forEach(function (selector) {
                var oldPart = self._container.find(selector).first();
                var newPart = fresh.find(selector).first();
                if (oldPart.length && newPart.length) {
                    nightshift.disposeTooltips(oldPart);
                    oldPart.replaceWith(newPart);
                }
            });
            this._place();

            setTimeout(function () { self._container.find('.ns-row-updated').removeClass('ns-row-updated'); }, 1000);
            return added;
        };

        // Replaces the whole list (rows without keys, or a table of another shape)
        LiveTable.prototype._replace = function (fresh) {
            // The buttons stay, with their tooltips; the tooltips of the rest go with the old rows
            this._button.detach();
            this._refreshButton.detach().removeClass('spinning');
            nightshift.disposeTooltips(this._container.children());
            this._container.html(fresh.html());
            this._container.find('table > tbody > tr').each(function () { $(this).data('ns-html', this.outerHTML); });
            this._place();
        };

        return LiveTable;
    })();

    // The live activity feed on the home page: polls /activity and prepends new transitions
    nightshift.ActivityFeed = (function () {
        // The state chips of the built-in states (HtmlHelper.StateLabel): colour group and icon
        var STATE_CHIPS = {
            Enqueued: ['enqueued', 'inbox-fill'], Scheduled: ['scheduled', 'clock'], Processing: ['processing', 'play-circle-fill'],
            Succeeded: ['succeeded', 'check-circle-fill'], Failed: ['failed', 'x-circle-fill'], Deleted: ['deleted', 'trash-fill'],
            Awaiting: ['awaiting', 'hourglass-split']
        };
        var MAX_ROWS = 40;

        function ActivityFeed(config) {
            this._list = $('.js-activity').first();
            if (!this._list.length) return;

            this._url = this._list.data('url');
            this._jobUrl = this._list.data('job-url');
            this._interval = Math.max(config.pollInterval || 2000, 1000);
            // Entries are stamped by the servers that wrote them, whose clocks may differ from each other and from
            // the dashboard's, so the next poll asks from a little before the newest entry received
            this._since = 0;
            this._seen = {};
            this._timer = null;
            this._paused = false;
            this._unannounced = 0;
            this._lastAnnounced = 0;
            this._status = $('.js-activity-status').first();
            this._summary = this._list.attr('data-text-summary') || '{0} new state changes';

            var self = this;
            this._poll(true);

            $(document).on('visibilitychange', function () {
                if (!document.hidden && !self._paused) self._poll(false);
            });

            // "Pause feed": no polls and no new rows until it runs again
            this._list.closest('.hf-card').find('.js-activity-pause').on('change', function () {
                self._paused = this.checked;
                $(this).closest('.hf-card').find('.js-activity-dot').toggleClass('paused', self._paused);
                clearTimeout(self._timer);
                if (!self._paused) self._poll(false);
            });
        }

        // One polite summary of what came in, at most every 15 seconds: a row read aloud as it comes would drown
        // everything else
        ActivityFeed.prototype._announce = function (count) {
            this._unannounced += count;
            var now = Date.now();
            if (!this._unannounced || now - this._lastAnnounced < 15000 || !this._status.length) return;
            this._status.text(format(this._summary, this._unannounced));
            this._unannounced = 0;
            this._lastAnnounced = now;
        };

        ActivityFeed.prototype._poll = function (initial) {
            var self = this;
            clearTimeout(this._timer);

            if (this._paused) return;

            if (document.hidden && !initial) {
                this._timer = setTimeout(function () { self._poll(false); }, this._interval);
                return;
            }

            $.ajax({ url: this._url, data: { count: initial ? MAX_ROWS : 50, since: this._since }, cache: false })
                .done(function (data) {
                    var entries = (data.entries || []).slice().reverse(); // oldest first, so the newest ends on top
                    // The servers may record the feed even when this process doesn't, so only an empty list with the
                    // feed off here gets the hint
                    if (initial && entries.length === 0 && data.enabled === false) {
                        self._list.find('.hf-activity-empty').text(self._list.data('off'));
                    }
                    var before = Object.keys(self._seen).length;
                    for (var i = 0; i < entries.length; i++) {
                        self._add(entries[i], !initial);
                    }
                    if (!initial) self._announce(Object.keys(self._seen).length - before);
                    for (var j = 0; j < entries.length; j++) {
                        self._since = Math.max(self._since, entries[j].t - ActivityFeed.SINCE_MARGIN_MS);
                    }
                    self._forget();
                    self._list.closest('.hf-card').find('.js-activity-dot').removeClass('offline');
                })
                // The app bar's status follows the statistics polls; the feed's own dot says when the feed can't load
                .fail(function () { self._list.closest('.hf-card').find('.js-activity-dot').addClass('offline'); })
                .always(function () {
                    if (!self._paused) self._timer = setTimeout(function () { self._poll(false); }, self._interval);
                });
        };

        // How far before the newest received entry the next poll starts; the entries seen twice are skipped
        ActivityFeed.SINCE_MARGIN_MS = 30 * 1000;

        // Polls ask only for entries from _since on, so older keys can't come back: drop them, or a wall screen
        // left open would keep millions
        ActivityFeed.prototype._forget = function () {
            var limit = this._since - ActivityFeed.SINCE_MARGIN_MS;
            for (var key in this._seen) {
                if (Object.prototype.hasOwnProperty.call(this._seen, key) && this._seen[key] < limit) {
                    delete this._seen[key];
                }
            }
        };

        ActivityFeed.prototype._add = function (entry, animate) {
            var key = entry.t + ':' + entry.j + ':' + entry.s;
            if (Object.prototype.hasOwnProperty.call(this._seen, key)) return;
            this._seen[key] = entry.t;

            this._list.find('.hf-activity-empty').remove();

            var row = $('<li>').addClass('hf-activity-row' + (animate ? ' hf-activity-new' : ''));
            row.append($('<time>').addClass('hf-activity-time').attr('title', NightShiftIntl.format(entry.t, 'dateTimeWithSeconds')).text(NightShiftIntl.format(entry.t, 'timeWithSeconds')));
            var chip = STATE_CHIPS[entry.s];
            row.append($('<span>').addClass('ns-state-chip hf-activity-state' + (chip ? ' ns-state-' + chip[0] : ''))
                .append(nightshift.icon(chip ? chip[1] : 'circle-fill')).append($('<span>').text(entry.s)));
            row.append($('<a>').addClass('hf-activity-job').attr('href', this._jobUrl + nightshift.routeValue(entry.j)).text('#' + entry.j));
            if (entry.m) row.append($('<code>').addClass('hf-activity-method').text(entry.m));
            if (entry.v) row.append($('<span>').addClass('text-muted hf-activity-server').text(entry.v));
            if (entry.r) row.append($('<span>').addClass('text-muted hf-activity-reason').attr('title', entry.r).text(entry.r));

            this._list.prepend(row);
            var old = this._list.children().slice(MAX_ROWS);
            nightshift.disposeTooltips(old);
            old.remove();

            if (animate) {
                setTimeout(function () { row.removeClass('hf-activity-new'); }, 1200);
            }
        };

        return ActivityFeed;
    })();

    // The runner dialog of the Job Catalog: builds a form from the method's parameters and creates the job
    nightshift.CatalogRunner = (function () {
        function CatalogRunner(modal) {
            this._modal = $(modal);
            this._form = this._modal.find('.js-catalog-form');
            this._arguments = this._modal.find('.js-catalog-arguments');
            this._title = this._modal.find('.js-catalog-title');
            this._id = this._modal.find('.js-catalog-id');
            this._source = this._modal.find('.js-catalog-source');
            this._error = this._modal.find('.js-catalog-error');
            this._success = this._modal.find('.js-catalog-success');
            this._submit = this._modal.find('.js-catalog-submit');
            this._cron = this._modal.find('.js-catalog-cron');
            this._cronDescription = this._modal.find('.js-catalog-cron-description');
            this._runUrl = this._modal.data('run-url');
            this._previewUrl = this._modal.data('preview-url');
            this._jobUrl = this._modal.data('job-url');
            this._recurringUrl = this._modal.data('recurring-url');
            this._text = {
                created: this._modal.data('text-created'),
                recurringCreated: this._modal.data('text-recurring-created'),
                injected: this._modal.data('text-injected'),
                redacted: this._modal.data('text-redacted') || 'redacted, enter the value again',
                utc: this._modal.data('text-utc') || 'in UTC'
            };
            this._previewTimer = null;

            var self = this;

            $(document).on('click', '.js-catalog-run', function (e) {
                e.preventDefault();
                self.open($(this));
            });

            this._modal.on('change', '.js-catalog-modes input', function () {
                self._setMode($(this).val());
            });

            this._cron.on('input', function () {
                clearTimeout(self._previewTimer);
                self._previewTimer = setTimeout(function () { self._describeCron(); }, 300);
            });

            this._form.on('submit', function (e) {
                e.preventDefault();
                self._run();
            });

            $(document).on('input', '.js-catalog-filter', function () {
                self._filter($(this).val());
            });
        }

        CatalogRunner.prototype.open = function (button) {
            var parameters = button.data('parameters') || [];

            this._id.val(button.attr('data-id') || '');
            this._source.val(button.attr('data-source') || '');
            // "Run again with changes" starts in the job's own queue; the Catalog page in "default"
            this._modal.find('[name="queue"]').val(button.attr('data-queue') || '');
            this._title.text(button.attr('data-type') + '.' + button.attr('data-method'));
            this._error.hide().text('');
            this._success.hide().empty();
            nightshift.setLoading(this._submit, false);
            this._arguments.empty();

            for (var i = 0; i < parameters.length; i++) {
                this._arguments.append(this._buildField(parameters[i], i));
            }

            this._modal.find('.js-catalog-modes input[value="enqueue"]').prop('checked', true);
            this._setMode('enqueue');
            var first = this._arguments.find('input, select, textarea').first();
            this._modal[0].addEventListener('shown.bs.modal', function () { first.trigger('focus'); }, { once: true });
            bootstrap.Modal.getOrCreateInstance(this._modal[0]).show();
        };

        CatalogRunner.prototype._buildField = function (parameter, index) {
            var id = 'catalogArg' + index;
            var name = 'arg:' + parameter.name;
            var group = $('<div>').addClass('mb-3');
            var label = $('<label>').attr('for', id).append(document.createTextNode(parameter.name + ' '))
                .append($('<span>').addClass('text-muted hf-param-type').text(parameter.type));
            var input;

            switch (parameter.input) {
                case 'checkbox':
                    // An unchecked box sends nothing, which would mean "the default", so a hidden "false" goes first;
                    // the server takes "true" when the box sends it too
                    input = $('<input type="checkbox" class="form-check-input" value="true">').attr({ id: id, name: name }).prop('checked', parameter['default'] === 'true');
                    group.addClass('form-check')
                        .append($('<input type="hidden" value="false">').attr('name', name))
                        .append(input)
                        .append($('<label class="form-check-label">').attr('for', id).text(parameter.name));
                    return group;
                case 'select':
                    var options = parameter.options || [];
                    var selected = parameter['default'] || '';
                    input = $('<select class="form-select">').attr({ id: id, name: name });
                    // bool? and nullable enums can be set to null explicitly; an empty value would mean "the default"
                    if (parameter.nullable) {
                        input.append($('<option>').val('null').text('(null)'));
                        if (selected === '') selected = 'null';
                    }
                    for (var i = 0; i < options.length; i++) {
                        input.append($('<option>').val(options[i]).text(options[i]));
                    }
                    if (parameter.multiple) {
                        // A [Flags] enum: its choices are combined on the server. The hidden "0" is sent even when
                        // nothing is chosen, so no choice means no flags rather than the default.
                        input.attr({ multiple: 'multiple', size: Math.min(8, input.children().length) });
                        input.val(selected ? selected.split(/\s*,\s*/) : []);
                        group.append($('<input type="hidden" value="0">').attr('name', name));
                    } else if (selected) {
                        input.val(selected);
                    }
                    break;
                case 'number':
                    input = $('<input type="number" step="any" class="form-control">').attr({ id: id, name: name }).val(parameter['default'] || '');
                    break;
                case 'datetime':
                    // The date picker holds a local date and time to the second. A value with "Z", an offset or a
                    // fraction of a second goes in a text input instead, so it is sent back exactly as the job had it.
                    var value = parameter['default'] || '';
                    if (value === '' || /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value)) {
                        input = $('<input type="datetime-local" step="1" class="form-control">').attr({ id: id, name: name }).val(value);
                        // A DateTimeOffset without an offset is read as UTC on the server
                        if (/^DateTimeOffset\??$/.test(parameter.type)) label.append(' ').append($('<span>').addClass('text-muted').text(this._text.utc));
                    } else {
                        input = $('<input type="text" class="form-control hf-mono">').attr({ id: id, name: name, autocomplete: 'off', spellcheck: 'false' }).val(value);
                    }
                    break;
                case 'json':
                    input = $('<textarea rows="3" class="form-control hf-mono" spellcheck="false">').attr({ id: id, name: name, placeholder: '{ "json": true }' }).val(parameter['default'] || '');
                    break;
                default:
                    input = $('<input type="text" class="form-control">').attr({ id: id, name: name, autocomplete: 'off', spellcheck: 'false' }).val(parameter['default'] || '');
            }

            if (parameter.hasDefault) input.attr('placeholder', parameter['default'] || 'null');

            if (parameter.redacted) {
                input.val('').attr('placeholder', this._text.redacted).attr('autocomplete', 'new-password');
                label.append(' ').append($('<span>').addClass('badge text-bg-secondary').text('redacted'));
            }

            return group.append(label).append(input);
        };

        CatalogRunner.prototype._setMode = function (mode) {
            this._modal.find('.js-catalog-modes label').each(function () {
                $(this).toggleClass('active', $(this).find('input').val() === mode);
            });
            this._modal.find('.js-catalog-mode-field').each(function () {
                $(this).toggle($(this).data('mode') === mode);
            });
        };

        CatalogRunner.prototype._describeCron = function () {
            var self = this;
            var cron = trim(this._cron.val());
            if (!cron) { this._cronDescription.text(''); return; }

            $.get(this._previewUrl, { cron: cron })
                .done(function (data) { self._cronDescription.text(data.error ? data.error : (data.description || data.cron || '')); })
                .fail(function () { self._cronDescription.text(''); });
        };

        CatalogRunner.prototype._run = function () {
            var self = this;
            nightshift.setLoading(this._submit, true);
            this._error.hide();
            this._success.hide();

            $.post(this._runUrl, this._form.serialize())
                .done(function (data) {
                    nightshift.setLoading(self._submit, false);
                    var message;
                    if (data.recurringId) {
                        message = $('<span>').text(self._text.recurringCreated.replace('{0}', data.recurringId) + ' ')
                            .append($('<a>').attr('href', self._recurringUrl + '?edit=' + encodeURIComponent(data.recurringId)).text(data.recurringId));
                    } else {
                        message = $('<span>').text(self._text.created.replace('{0}', data.jobId) + ' ')
                            .append($('<a>').attr('href', self._jobUrl + nightshift.routeValue(data.jobId)).text('#' + data.jobId));
                    }
                    self._success.empty().append(message).show();
                })
                .fail(function (xhr) {
                    nightshift.setLoading(self._submit, false);
                    var message = xhr.responseJSON && xhr.responseJSON.error
                        ? xhr.responseJSON.error
                        : nightshift.describeRequestError(xhr);
                    self._error.text(message).show();
                });
        };

        CatalogRunner.prototype._filter = function (text) {
            var needle = trim(text).toLowerCase();
            $('.js-catalog-entry').each(function () {
                var row = $(this);
                row.toggle(!needle || String(row.attr('data-search') || '').toLowerCase().indexOf(needle) >= 0);
            });
            $('.js-catalog-group').each(function () {
                var group = $(this);
                group.toggle(group.find('.js-catalog-entry:visible').length > 0);
            });
        };

        return CatalogRunner;
    })();

    // Small operator actions: pausing processing with a reason, copying a job as C#, failure clusters,
    // recurring job manifests, the tag filter
    nightshift.Operations = (function () {
        var NOTE_PREFIX = 'nightshift.note:';

        function Operations() {
            this._initClusters();
            this._initManifest();
            this._initTags();
            this._initDownloads();
            this._initNotes();

            $(document).on('submit', '.js-drain-form', function (e) {
                e.preventDefault();
                var form = $(this);
                var button = nightshift.setLoading(form.find('button[type=submit]'), true);
                $.post(form.data('url'), form.serialize())
                    .done(function () { window.location.reload(); })
                    .fail(function (xhr) { nightshift.setLoading(button, false); nightshift.showRequestError(xhr, commandName(button), function () { form.trigger('submit'); }); });
            });

            // A copy button (Html.CopyButton): its data-copy goes to the clipboard, and a snackbar says so
            $(document).on('click', '.js-copy', function (e) {
                e.preventDefault();
                var text = $(this).attr('data-copy') || '';
                var done = function () { nightshift.snackbar.show({ text: TEXT.copied }); };

                if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(text).then(done, function () { Operations.fallbackCopy(text); done(); });
                } else {
                    Operations.fallbackCopy(text);
                    done();
                }
            });

            // A note that can be dismissed (ns-note js-note with data-note, a key) stays away in this browser
            $('.js-note[data-note]').each(function () {
                try {
                    if (window.localStorage.getItem(NOTE_PREFIX + $(this).attr('data-note')) === 'dismissed') $(this).prop('hidden', true);
                } catch (err) { /* no storage: the note shows */ }
            });

            $(document).on('click', '.js-note-dismiss', function (e) {
                e.preventDefault();
                var note = $(this).closest('.js-note');
                nightshift.disposeTooltips(note);
                note.prop('hidden', true);
                try { window.localStorage.setItem(NOTE_PREFIX + note.attr('data-note'), 'dismissed'); } catch (err) { /* hidden until the page is left */ }
                focusBack(document.getElementById('content'));
            });

            $(document).on('click', '.js-copy-code', function (e) {
                e.preventDefault();
                var button = $(this);
                var code = button.attr('data-code') || '';
                var original = button.html();
                var done = function () {
                    nightshift.disposeTooltips(button.children());
                    button.addClass('btn-success').html(nightshift.icon('check-lg')).append(document.createTextNode(' ' + button.data('copied')));
                    setTimeout(function () { button.removeClass('btn-success').html(original); }, 1500);
                };

                if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(code).then(done, function () { Operations.fallbackCopy(code); done(); });
                } else {
                    Operations.fallbackCopy(code);
                    done();
                }
            });
        }

        // A cluster's commands post its jobs (data-jobs, JSON); a row action (js-job-command, Html.JobCommandButton)
        // posts its job (data-job)
        Operations.prototype._initClusters = function () {
            $(document).on('click', '.js-cluster-command, .js-job-command', function (e) {
                e.preventDefault();
                var button = $(this);
                var jobs = button.attr('data-job') !== undefined ? [button.attr('data-job')] : (button.data('jobs') || []);
                nightshift.confirmButton(button, jobs.length).then(function (confirmed) {
                    if (!confirmed) return;
                    var name = commandName(button);
                    var run = function () {
                        nightshift.setLoading(button, true);
                        $.post(button.attr('data-url'), { 'jobs[]': jobs })
                            .done(function () {
                                if (name) nightshift.snackbar.showAfterReload({ text: format(TEXT.done, name) });
                                window.location.reload();
                            })
                            .fail(function (xhr) { nightshift.setLoading(button, false); nightshift.showRequestError(xhr, name, run); });
                    };
                    run();
                });
            });
        };

        Operations.prototype._initManifest = function () {
            $(document).on('click', '.js-manifest-export', function (e) {
                e.preventDefault();
                var button = $(this);
                $.ajax({ url: button.data('url'), cache: false, dataType: 'text' }).done(function (text) {
                    var blob = new Blob([text], { type: 'application/json' });
                    var link = document.createElement('a');
                    link.href = URL.createObjectURL(blob);
                    link.download = 'nightshift-recurring-jobs.json';
                    document.body.appendChild(link);
                    link.click();
                    setTimeout(function () { URL.revokeObjectURL(link.href); link.remove(); }, 0);
                }).fail(function (xhr) { nightshift.showRequestError(xhr); });
            });

            var modal = $('#manifestImport');
            if (!modal.length) return;

            $(document).on('click', '.js-manifest-import', function (e) {
                e.preventDefault();
                modal.find('.js-manifest-error, .js-manifest-result').hide();
                bootstrap.Modal.getOrCreateInstance(modal[0]).show();
            });

            modal.on('change', '.js-manifest-file', function () {
                var file = this.files && this.files[0];
                if (!file) return;
                var reader = new FileReader();
                reader.onload = function () { modal.find('.js-manifest-text').val(reader.result); };
                reader.readAsText(file);
            });

            modal.on('submit', '.js-manifest-form', function (e) {
                e.preventDefault();
                var form = $(this);
                var button = nightshift.setLoading(form.find('.js-manifest-apply'), true);
                var error = modal.find('.js-manifest-error').hide();
                var result = modal.find('.js-manifest-result').hide();

                $.post(modal.data('import-url'), form.serialize())
                    .done(function (data) {
                        nightshift.setLoading(button, false);
                        var text = String(modal.data('text-result'))
                            .replace('{0}', (data.added || []).length)
                            .replace('{1}', (data.updated || []).length)
                            .replace('{2}', (data.removed || []).length)
                            .replace('{3}', (data.errors || []).length);
                        result.empty().append($('<strong>').text(text));
                        if ((data.errors || []).length) {
                            var list = $('<ul class="hf-manifest-errors">');
                            data.errors.forEach(function (item) { list.append($('<li>').append($('<code>').text(item.id)).append(document.createTextNode(' ' + item.message))); });
                            result.append(list);
                        }
                        result.show();
                        if (!(data.errors || []).length) setTimeout(function () { window.location.reload(); }, 1200);
                    })
                    .fail(function (xhr) {
                        nightshift.setLoading(button, false);
                        error.text(nightshift.describeRequestError(xhr)).show();
                    });
            });
        };

        Operations.prototype._initDownloads = function () {
            $(document).on('click', '.js-download', function (e) {
                e.preventDefault();
                var button = $(this);
                $.ajax({ url: button.data('url'), cache: false, dataType: 'text' }).done(function (text, status, xhr) {
                    var type = (xhr.getResponseHeader('Content-Type') || 'text/plain').split(';')[0];
                    var blob = new Blob([text], { type: type });
                    var link = document.createElement('a');
                    link.href = URL.createObjectURL(blob);
                    link.download = button.data('filename') || 'download';
                    document.body.appendChild(link);
                    link.click();
                    setTimeout(function () { URL.revokeObjectURL(link.href); link.remove(); }, 0);
                }).fail(function (xhr) { nightshift.showRequestError(xhr); });
            });
        };

        Operations.prototype._initNotes = function () {
            var submit = function (form, ack) {
                var button = nightshift.setLoading(form.find(ack ? '.js-note-ack' : 'button[type=submit]'), true);
                var data = form.serializeArray();
                if (ack) data.push({ name: 'ack', value: 'true' });
                $.post(form.data('url'), data)
                    .done(function () { window.location.reload(); })
                    .fail(function (xhr) { nightshift.setLoading(button, false); nightshift.showRequestError(xhr); });
            };
            $(document).on('submit', '.js-note-form', function (e) { e.preventDefault(); submit($(this), false); });
            $(document).on('click', '.js-note-ack', function (e) { e.preventDefault(); submit($(this).closest('form'), true); });
        };

        Operations.prototype._initTags = function () {
            // The filter runs on the server over every tag (the list is paged), so Enter submits the form; clearing
            // the box with its clear button shows every tag again
            $(document).on('search', '.js-tags-filter', function () {
                if (trim($(this).val()) === '') $(this).closest('form').trigger('submit');
            });
        };

        Operations.fallbackCopy = function (text) {
            var area = $('<textarea>').val(text).css({ position: 'fixed', opacity: 0 }).appendTo('body');
            area[0].select();
            try { document.execCommand('copy'); } catch (err) { /* not available */ }
            area.remove();
        };

        return Operations;
    })();
    // Jobs the viewer follows: a sidebar panel that polls their state and a browser notification when one finishes
    nightshift.Watchlist = (function () {
        var FINAL = { Succeeded: true, Failed: true, Deleted: true };

        function Watchlist(config) {
            this._panel = $('.js-watchlist').first();
            if (!this._panel.length) return;

            // One list per dashboard: two dashboards on the same site (one per storage) have different job ids
            this._storageKey = 'nightshift.watch:' + String((config && config.pollUrl) || '').replace(/\/stats$/, '');

            this._liveUrl = this._panel.data('live-url');
            this._jobUrl = this._panel.data('job-url');
            this._text = { title: this._panel.attr('data-text-title'), empty: this._panel.attr('data-text-empty'), done: this._panel.attr('data-text-done'), remove: this._panel.attr('data-text-remove') || 'Stop watching job {0}' };
            this._items = this._read();
            this._interval = Math.max((config && config.pollInterval) || 2000, 2000);
            this._timer = null;

            var self = this;
            this._build();
            this._render();
            this._syncButtons();
            this._schedule();

            $(document).on('click', '.js-watch-toggle', function (e) {
                e.preventDefault();
                var button = $(this);
                self.toggle(button.attr('data-id') || '', button.attr('data-name') || '', button.attr('data-state'));
            });

            this._panel.on('click', '.js-watch-remove', function (e) {
                e.preventDefault();
                self.remove($(this).attr('data-id') || '');
            });
        }

        Watchlist.prototype._read = function () {
            try { return JSON.parse(window.localStorage.getItem(this._storageKey) || '[]'); } catch (e) { return []; }
        };

        Watchlist.prototype._write = function () {
            try { window.localStorage.setItem(this._storageKey, JSON.stringify(this._items)); } catch (e) { /* private mode */ }
        };

        Watchlist.prototype._build = function () {
            this._panel.empty()
                .append($('<div class="hf-watchlist-title">').append(nightshift.icon('eye-fill')).append(document.createTextNode(' ' + this._text.title)))
                .append(this._list = $('<ul class="hf-watchlist-items">'));
        };

        Watchlist.prototype._render = function () {
            this._panel.toggle(this._items.length > 0);
            this._list.empty();
            for (var i = 0; i < this._items.length; i++) {
                var item = this._items[i];
                var li = $('<li class="hf-watchlist-item">').toggleClass('done', !!FINAL[item.state]);
                li.append($('<a>').attr('href', this._jobUrl + nightshift.routeValue(item.id)).append($('<span class="hf-watchlist-id">').text('#' + item.id)).append($('<span class="hf-watchlist-name">').text(item.name || '')));
                li.append($('<span class="hf-watchlist-state">').addClass('state-' + String(item.state || '').toLowerCase()).text(item.state || '…'));
                li.append($('<button type="button" class="ns-icon-button hf-watchlist-remove js-watch-remove">').attr({ 'data-id': item.id, 'aria-label': format(this._text.remove, '#' + item.id) }).html(nightshift.icon('x-lg')));
                this._list.append(li);
            }
        };

        Watchlist.prototype._syncButtons = function () {
            var self = this;
            $('.js-watch-toggle').each(function () {
                var button = $(this);
                var watching = self._find(button.attr('data-id') || '') >= 0;
                button.toggleClass('active btn-info', watching).toggleClass('btn-outline-secondary', !watching)
                    .attr('aria-pressed', watching ? 'true' : 'false');
                button.find('.js-watch-label').text(watching ? button.data('text-watching') : button.data('text-watch'));
            });
        };

        Watchlist.prototype._find = function (id) {
            for (var i = 0; i < this._items.length; i++) if (this._items[i].id === id) return i;
            return -1;
        };

        Watchlist.prototype.toggle = function (id, name, state) {
            var index = this._find(id);
            if (index >= 0) { this._items.splice(index, 1); }
            else {
                this._items.unshift({ id: id, name: name, state: state || null });
                if (this._items.length > 20) this._items.length = 20;
                if (window.Notification && Notification.permission === 'default') Notification.requestPermission();
            }
            this._write();
            this._render();
            this._syncButtons();
            this._schedule();
        };

        Watchlist.prototype.remove = function (id) {
            var index = this._find(id);
            if (index >= 0) this._items.splice(index, 1);
            this._write();
            this._render();
            this._syncButtons();
        };

        Watchlist.prototype._schedule = function () {
            var self = this;
            clearTimeout(this._timer);
            var pending = this._items.filter(function (item) { return !FINAL[item.state]; });
            if (!pending.length) return;
            this._timer = setTimeout(function () { self._poll(pending); }, this._interval);
        };

        Watchlist.prototype._poll = function (pending) {
            var self = this;
            $.ajax({ url: this._liveUrl, data: { jobs: pending.map(function (p) { return p.id; }).join(',') }, cache: false })
                .done(function (data) {
                    var jobs = (data && data.jobs) || {};
                    for (var i = 0; i < self._items.length; i++) {
                        var item = self._items[i];
                        var live = jobs[item.id];
                        if (!live) continue;
                        if (live.state !== item.state) {
                            item.state = live.state;
                            if (FINAL[live.state]) self._notify(item);
                        }
                    }
                    self._write();
                    self._render();
                })
                .always(function () { self._schedule(); });
        };

        Watchlist.prototype._notify = function (item) {
            var text = String(this._text.done).replace('{0}', '#' + item.id).replace('{1}', item.state);
            nightshift.snackbar.show({
                text: text + (item.name ? ' — ' + item.name : ''),
                kind: item.state === 'Failed' ? 'error' : null,
                duration: 10000,
                action: { label: format(TEXT.viewJob, '#' + item.id), href: this._jobUrl + nightshift.routeValue(item.id) }
            });

            if (window.Notification && Notification.permission === 'granted') {
                try { new Notification(text, { body: item.name || '', tag: 'nightshift-' + item.id }); } catch (e) { /* not available */ }
            }
        };

        return Watchlist;
    })();

    // Whether the single-key shortcuts work, chosen per browser in the shortcuts help or the command palette
    nightshift.singleKeyShortcuts = (function () {
        var STORAGE_KEY = 'nightshift.singleKeyShortcuts';
        // Without storage (blocked site data) the choice lasts until the page is left
        var current = null;

        return {
            enabled: function () {
                try { return window.localStorage.getItem(STORAGE_KEY) !== 'off'; } catch (e) { return current !== 'off'; }
            },
            set: function (value) {
                current = value ? null : 'off';
                try {
                    if (value) window.localStorage.removeItem(STORAGE_KEY);
                    else window.localStorage.setItem(STORAGE_KEY, 'off');
                } catch (e) { /* kept in memory */ }
            }
        };
    })();

    // j/k/x/Enter/r/d on job lists, and the "?" overlay that lists every shortcut
    nightshift.Shortcuts = (function () {
        var ROWS = '.js-jobs-list-row';

        function Shortcuts(config) {
            this._overlay = null;
            this._index = -1;
            var self = this;

            $(document).on('keydown', function (e) {
                if (e.key === 'Escape' && self._overlay && self._overlay.is(':visible')) { self.toggle(); return; }
                if (nightshift.sheet.isOpen()) return;
                if ($(e.target).is('input, textarea, select, [contenteditable]')) return;
                if (e.ctrlKey || e.metaKey || e.altKey) return;
                if ($('.hf-palette:visible').length) return;
                if (!nightshift.singleKeyShortcuts.enabled()) return;
                // "g" then a letter goes to a page; that letter isn't a list shortcut
                if (e.key === 'g' || (nightshift.CommandPalette && nightshift.CommandPalette.isGoPending())) return;

                var rows = $(ROWS);
                switch (e.key) {
                    case 'j': if (rows.length) { self._move(rows, 1); e.preventDefault(); } break;
                    case 'k': if (rows.length) { self._move(rows, -1); e.preventDefault(); } break;
                    case 'x': if (self._current(rows).length) { self._current(rows).find('.js-jobs-list-checkbox').trigger('click'); e.preventDefault(); } break;
                    case 'Enter': if (self._current(rows).length) { var link = self._current(rows).find('a[href*="/jobs/details/"]').first(); if (link.length) window.location.href = link.attr('href'); } break;
                    case 'r': self._command('.js-jobs-list-command[data-list-primary], .js-jobs-list-command.btn-primary'); break;
                    case 'd': self._command('.js-jobs-list-command[data-url$="/delete"]'); break;
                }
            });
        }

        Shortcuts.prototype._current = function (rows) {
            return this._index >= 0 && this._index < rows.length ? rows.eq(this._index) : $();
        };

        Shortcuts.prototype._move = function (rows, delta) {
            rows.removeClass('hf-row-focus');
            this._index = Math.max(0, Math.min(rows.length - 1, this._index + delta));
            var row = rows.eq(this._index).addClass('hf-row-focus');
            row[0].scrollIntoView({ block: 'nearest' });
        };

        // A key press is easy to make by mistake, so a command without its own confirmation (Requeue, Trigger now,
        // Enqueue now) asks first; one with a data-confirm text asks itself when clicked
        Shortcuts.prototype._command = function (selector) {
            var button = $(selector).first();
            if (!button.length || button.prop('disabled')) return;

            var count = $("input[name='jobs[]']:checked").length;
            if (button.attr('data-confirm')) {
                button.trigger('click');
                return;
            }

            nightshift.confirm({ title: format(TEXT.shortcutConfirm, commandName(button), count), confirmLabel: commandName(button) })
                .then(function (confirmed) { if (confirmed) button.trigger('click'); });
        };

        Shortcuts.prototype.toggle = function () {
            if (!this._overlay) {
                var rows = [
                    ['Ctrl K', '/', TEXT.shortcutSearch],
                    ['g then h / j / p / f / r / s / i / c / t / b / a', '', TEXT.shortcutGo],
                    ['j / k', '', TEXT.shortcutMove],
                    ['x', '', TEXT.shortcutSelect],
                    ['Enter', '', TEXT.shortcutOpen],
                    ['r', '', TEXT.shortcutRun],
                    ['d', '', TEXT.shortcutDelete],
                    ['?', '', TEXT.shortcutHelp],
                    ['Esc', '', TEXT.close]
                ];
                var table = $('<table class="hf-shortcuts-table">');
                rows.forEach(function (r) {
                    var keys = $('<td class="hf-shortcuts-keys">');
                    r[0].split(' / ').forEach(function (k, i) { if (i) keys.append(' / '); keys.append($('<kbd>').text(k)); });
                    if (r[1]) keys.append(' · ').append($('<kbd>').text(r[1]));
                    table.append($('<tr>').append(keys).append($('<td>').text(r[2])));
                });
                // Single-key shortcuts can be turned off, for speech input and accidental key presses (WCAG 2.1.4)
                var toggle = $('<input type="checkbox" role="switch" class="ns-switch" id="hfSingleKeyShortcuts">').prop('checked', nightshift.singleKeyShortcuts.enabled())
                    .on('change', function () { nightshift.singleKeyShortcuts.set(this.checked); });
                var setting = $('<label class="ns-switch-label hf-shortcuts-setting" for="hfSingleKeyShortcuts">').append(toggle)
                    .append($('<span>').text(TEXT.shortcutSingleKey));
                var self = this;
                var close = $('<button type="button" class="btn ns-btn-text">').text(TEXT.close).on('click', function () { self.toggle(); });
                this._overlay = $('<div class="hf-palette hf-shortcuts" role="dialog" aria-modal="true" aria-labelledby="hfShortcutsTitle" style="display: none">')
                    .append(this._box = $('<div class="hf-palette-box hf-shortcuts-box" tabindex="-1">')
                        .append($('<h2 id="hfShortcutsTitle">').text(TEXT.shortcuts))
                        .append(table).append(setting)
                        .append($('<div class="modal-footer px-0 pb-0">').append(close)));
                this._overlay.on('click', function (e) { if (e.target === self._overlay[0]) self.toggle(); });
                this._overlay.on('keydown', function (e) { trapTab(e, self._box[0]); });
                $('body').append(this._overlay);
            }

            if (this._overlay.is(':visible')) {
                this._overlay.hide();
                var target = this._returnFocus;
                this._returnFocus = null;
                focusBack(target);
            } else {
                this._returnFocus = document.activeElement;
                this._overlay.find('#hfSingleKeyShortcuts').prop('checked', nightshift.singleKeyShortcuts.enabled());
                this._overlay.show();
                this._box.trigger('focus');
            }
        };
        return Shortcuts;
    })();

    // The chain flowchart: zoom in steps, fit to the width, full screen
    NightShift.ChainFlow = (function () {
        var zoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.25, 1.5, 2];

        function ChainFlow(element) {
            this._element = element;
            this._viewport = element.querySelector('.js-flow-viewport');
            this._sizer = element.querySelector('.js-flow-sizer');
            this._stage = element.querySelector('.js-flow-stage');
            this._level = element.querySelector('.js-flow-level');
            if (!this._viewport || !this._sizer || !this._stage) throw new Error('The chain flowchart is missing its viewport, sizer or stage.');

            this._width = parseInt(this._stage.getAttribute('data-width'), 10) || 0;
            this._height = parseInt(this._stage.getAttribute('data-height'), 10) || 0;
            this._zoom = 1;
            // Until the user zooms, the chart follows the width of the window
            this._fitted = true;

            var self = this;
            $(element).on('click', '.js-flow-zoom', function () {
                var mode = $(this).data('zoom');
                if (mode === 'fit') {
                    self.fit();
                } else {
                    self.step(mode === 'in' ? 1 : -1);
                }
            });
            $(element).on('click', '.js-flow-fullscreen', function () {
                self.toggleFullscreen();
            });
            $(document).on('keydown', function (e) {
                if (e.key === 'Escape' && $(element).hasClass('hf-flow-fullscreen')) self.toggleFullscreen();
            });

            var resizeTimer = null;
            $(window).on('resize', function () {
                if (!self._fitted) return;
                clearTimeout(resizeTimer);
                resizeTimer = setTimeout(function () { self.fit(); }, 100);
            });

            this.fit();
        }

        // A chart that can't be built is left as it is drawn, without stopping the rest of the page
        ChainFlow.create = function (element) {
            try {
                return new ChainFlow(element);
            } catch (e) {
                if (window.console && console.error) console.error('The chain flowchart could not be set up.', e);
                return null;
            }
        };

        ChainFlow.prototype.fit = function () {
            var available = this._viewport.clientWidth - 2;
            this.zoom(this._width > 0 && available > 0 ? Math.min(1, available / this._width) : 1);
            this._fitted = true;
        };

        ChainFlow.prototype.step = function (direction) {
            var next = this._zoom;
            var i;
            if (direction > 0) {
                for (i = 0; i < zoomSteps.length; i++) {
                    if (zoomSteps[i] > this._zoom + 0.001) { next = zoomSteps[i]; break; }
                }
            } else {
                for (i = zoomSteps.length - 1; i >= 0; i--) {
                    if (zoomSteps[i] < this._zoom - 0.001) { next = zoomSteps[i]; break; }
                }
            }

            this.zoom(next);
            this._fitted = false;
        };

        ChainFlow.prototype.zoom = function (value) {
            this._zoom = Math.max(0.1, Math.min(2, value));
            this._stage.style.transform = 'scale(' + this._zoom + ')';
            this._sizer.style.width = Math.round(this._width * this._zoom) + 'px';
            this._sizer.style.height = Math.round(this._height * this._zoom) + 'px';
            if (this._level) this._level.textContent = Math.round(this._zoom * 100) + '%';
        };

        ChainFlow.prototype.toggleFullscreen = function () {
            var on = !$(this._element).hasClass('hf-flow-fullscreen');
            $(this._element).toggleClass('hf-flow-fullscreen', on);
            $(this._element).find('.js-flow-fullscreen').attr('aria-pressed', on ? 'true' : 'false');
            this.fit();
        };

        return ChainFlow;
    })();
})(window.NightShift = window.NightShift || {});

$(function () {
    NightShift.page = new NightShift.Page(NightShift.config);
});
