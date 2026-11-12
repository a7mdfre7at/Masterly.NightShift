using System.Net;
using System.Text;

namespace Playground;

/// <summary>The guide: the experiments by section, each with its code and a button, and the event log.</summary>
public static class HomePage
{
    public static string Render(IReadOnlyList<Experiment> experiments)
    {
        List<IGrouping<string, Experiment>> sections = [.. experiments.GroupBy(static x => x.Section)];
        StringBuilder nav = new();
        StringBuilder body = new();

        foreach (IGrouping<string, Experiment> section in sections)
        {
            string anchor = Slug(section.Key);
            nav.Append($"<a href=\"#{anchor}\">{Encode(section.Key)}</a>");
            body.Append($"<h2 id=\"{anchor}\">{Encode(section.Key)}</h2>");

            foreach (Experiment experiment in section)
            {
                body.Append($"<article class=\"card\" id=\"{Encode(experiment.Id)}\">");
                body.Append($"<header><h3>{Encode(experiment.Title)}</h3>");
                if (experiment.Run != null)
                {
                    body.Append($"<button data-run=\"{Encode(experiment.Id)}\">{Encode(experiment.Button)}</button>");
                }

                body.Append("</header>");
                body.Append($"<p>{Encode(experiment.What)}</p>");
                body.Append($"<pre><code>{Encode(experiment.Code)}</code></pre>");
                body.Append($"<p class=\"look\"><strong>Look:</strong> {experiment.Look}</p>");
                body.Append($"<div class=\"result\" id=\"result-{Encode(experiment.Id)}\"></div>");
                body.Append("</article>");
            }
        }

        return Template
            .Replace("{{nav}}", nav.ToString(), StringComparison.Ordinal)
            .Replace("{{body}}", body.ToString(), StringComparison.Ordinal);
    }

    private static string Slug(string value) => new([.. value.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>NightShift Playground</title>
        <style>
          :root { --bg: #071a1f; --surface: #0c262d; --surface-2: #10313a; --border: #1d4651; --text: #e6f2f0; --muted: #8fb1ad; --accent: #5ee6c3; --danger: #ff7a90; --warn: #ffc56b; --info: #7cc4ff; }
          @media (prefers-color-scheme: light) { :root { --bg: #f4f8f7; --surface: #ffffff; --surface-2: #eef5f3; --border: #cfe0dc; --text: #10292e; --muted: #557370; --accent: #0b8f72; --danger: #c8334d; --warn: #a86a00; --info: #1f6fb2; } }
          * { box-sizing: border-box; }
          body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif; }
          a { color: var(--accent); }
          kbd { border: 1px solid var(--border); border-radius: 4px; padding: 0 5px; font-size: 12px; background: var(--surface-2); }
          .layout { display: grid; grid-template-columns: 230px minmax(0, 1fr) 380px; min-height: 100vh; }
          nav { position: sticky; top: 0; height: 100vh; overflow: auto; padding: 20px 16px; border-right: 1px solid var(--border); background: var(--surface); }
          nav h1 { font-size: 17px; margin: 0 0 4px; }
          nav p { margin: 0 0 16px; color: var(--muted); font-size: 13px; }
          nav a { display: block; padding: 6px 8px; border-radius: 6px; color: var(--text); text-decoration: none; font-size: 14px; }
          nav a:hover { background: var(--surface-2); }
          nav .dash { margin-top: 16px; display: block; text-align: center; background: var(--accent); color: var(--bg); font-weight: 600; }
          main { padding: 24px 28px 80px; max-width: 980px; }
          main > p.intro { color: var(--muted); }
          h2 { margin: 36px 0 12px; font-size: 20px; }
          .card { background: var(--surface); border: 1px solid var(--border); border-radius: 10px; padding: 14px 18px; margin-bottom: 14px; }
          .card header { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
          .card h3 { margin: 0; font-size: 16px; }
          .card p { margin: 8px 0; }
          .card .look { color: var(--muted); }
          .card .look strong { color: var(--text); }
          pre { margin: 8px 0; padding: 10px 12px; background: var(--surface-2); border-radius: 8px; overflow-x: auto; font: 12.5px/1.5 ui-monospace, "Cascadia Code", Consolas, monospace; }
          button { flex: none; cursor: pointer; border: 0; border-radius: 6px; padding: 6px 14px; background: var(--accent); color: var(--bg); font-weight: 600; }
          button:disabled { opacity: .5; cursor: wait; }
          .result:empty { display: none; }
          .result { margin-top: 8px; padding: 8px 12px; border-left: 3px solid var(--accent); background: var(--surface-2); border-radius: 4px; white-space: pre-wrap; font-size: 14px; }
          .result.error { border-left-color: var(--danger); }
          aside { position: sticky; top: 0; height: 100vh; display: flex; flex-direction: column; border-left: 1px solid var(--border); background: var(--surface); }
          aside header { display: flex; justify-content: space-between; align-items: center; padding: 14px 16px; border-bottom: 1px solid var(--border); }
          aside h2 { margin: 0; font-size: 16px; }
          aside header button { background: transparent; color: var(--muted); border: 1px solid var(--border); }
          aside .hint { padding: 8px 16px; margin: 0; color: var(--muted); font-size: 12.5px; border-bottom: 1px solid var(--border); }
          #events { flex: 1; overflow: auto; padding: 8px 12px; font: 12px/1.45 ui-monospace, Consolas, monospace; }
          .event { padding: 6px 4px; border-bottom: 1px solid var(--border); word-break: break-word; }
          .event time { color: var(--muted); margin-right: 6px; white-space: nowrap; }
          .event b { margin-right: 6px; }
          .src-alert b { color: var(--danger); } .src-webhook b { color: var(--warn); } .src-telemetry b { color: var(--info); } .src-exception b { color: var(--danger); } .src-function b { color: var(--accent); }
          @media (max-width: 1200px) { .layout { grid-template-columns: 200px minmax(0, 1fr); } aside { position: static; height: 420px; grid-column: 1 / -1; } }
          @media (max-width: 760px) { .layout { display: block; } nav { position: static; height: auto; } main { padding: 16px; } }
        </style>
        </head>
        <body>
        <div class="layout">
          <nav>
            <h1>NightShift Playground</h1>
            <p>Press a button, then look where the card says.</p>
            {{nav}}
            <a class="dash" href="/nightshift" target="_blank">Open the dashboard</a>
          </nav>
          <main>
            <p class="intro">Each card shows one feature: what it is, the code that uses it, and where to look in the dashboard. The jobs live in memory, so restarting the app starts clean. Work down the page in order the first time; later, jump to any section.</p>
            {{body}}
          </main>
          <aside>
            <header><h2>Event log</h2><button id="clear">Clear</button></header>
            <p class="hint">What happens outside the dashboard: alerts, webhook payloads, the exception handler, job functions and telemetry spans.</p>
            <div id="events"></div>
          </aside>
        </div>
        <script>
          document.querySelectorAll('button[data-run]').forEach(button => {
            button.addEventListener('click', async () => {
              const id = button.dataset.run;
              const result = document.getElementById('result-' + id);
              button.disabled = true;
              result.classList.remove('error');
              result.textContent = 'Running…';
              try {
                const response = await fetch('/try/' + encodeURIComponent(id), { method: 'POST' });
                const data = await response.json();
                result.innerHTML = data.html;
                result.classList.toggle('error', !data.ok);
              } catch (e) {
                result.textContent = 'The request failed: ' + e;
                result.classList.add('error');
              } finally {
                button.disabled = false;
              }
            });
          });

          let last = 0;
          const events = document.getElementById('events');
          async function poll() {
            try {
              const response = await fetch('/events?after=' + last);
              const entries = await response.json();
              for (const entry of entries) {
                last = Math.max(last, entry.id);
                const div = document.createElement('div');
                div.className = 'event src-' + entry.source.split(' ')[0];
                const time = document.createElement('time');
                time.textContent = new Date(entry.at).toLocaleTimeString();
                const source = document.createElement('b');
                source.textContent = entry.source;
                div.append(time, source, document.createTextNode(entry.text));
                events.prepend(div);
              }
            } catch (e) { }
            setTimeout(poll, 1500);
          }
          poll();
          document.getElementById('clear').addEventListener('click', async () => {
            await fetch('/events/clear', { method: 'POST' });
            events.textContent = '';
          });
        </script>
        </body>
        </html>
        """;
}
