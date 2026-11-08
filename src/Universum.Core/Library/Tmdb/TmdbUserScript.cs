using System.Globalization;
using Universum.Core.Text;

namespace Universum.Core.Library.Tmdb;

/// <summary>
/// UNIVERSUM's script for the browser (owner, 8. 11. 2026; Tampermonkey or Violentmonkey run it): on a page of a film or
/// series of TMDB it sends the page to the Zásobník TMDB by itself (UNIVERSUM takes it only while its automatic gathering is
/// switched on – 1.1), and shows the button "Do UNIVERSUM" (when UNIVERSUM
/// does not answer, the button starts it by the address universum://). It runs only on TMDB's film and series pages and
/// talks only to 127.0.0.1 with the key of this installation.
/// </summary>
public static class TmdbUserScript
{
    public const string Version = "1.2";

    public static string Text(int port, string key, bool automatic = true)
    {
        var button = Js(CoreText.T("＋ Do UNIVERSUM"));
        var added = Js(CoreText.T("✓ V zásobníku UNIVERSUM"));
        var refused = Js(CoreText.T("UNIVERSUM to neprijal"));
        var opening = Js(CoreText.T("Otváram UNIVERSUM…"));
        var silent = Js(CoreText.T("UNIVERSUM neodpovedá"));
        return $$"""
            // ==UserScript==
            // @name         UNIVERSUM – Zásobník TMDB
            // @namespace    https://github.com/tarzanislav/universum
            // @version      {{Version}}
            // @description  Filmy a seriály z TMDB do Zásobníka TMDB v UNIVERSUM – samy aj tlačidlom. Posiela len na tento počítač (127.0.0.1).
            // @match        https://www.themoviedb.org/movie/*
            // @match        https://www.themoviedb.org/tv/*
            // @grant        GM_xmlhttpRequest
            // @connect      127.0.0.1
            // @run-at       document-idle
            // @noframes
            // ==/UserScript==

            (function () {
              'use strict';
              const PORT = {{port.ToString(CultureInfo.InvariantCulture)}};
              const KEY = '{{key}}';
              const AUTOMATIC = {{(automatic ? "true" : "false")}};
              if (!/^\/(movie|tv)\/\d+/.test(location.pathname)) {
                return;
              }

              function send(button) {
                GM_xmlhttpRequest({
                  method: 'POST',
                  url: 'http://127.0.0.1:' + PORT + '/tmdb',
                  headers: { 'Content-Type': 'application/json', 'X-Universum-Key': KEY },
                  data: JSON.stringify({ u: location.href, t: document.title, a: !button }),
                  timeout: 3000,
                  onload: function (answer) {
                    if (button) {
                      button.textContent = answer.status === 204 ? {{added}} : {{refused}} + ' (' + answer.status + ')';
                    }
                  },
                  onerror: function () {
                    if (button) {
                      // UNIVERSUM does not run: the address universum:// starts it with the page
                      button.textContent = {{opening}};
                      location.href = 'universum://tmdb?u=' + encodeURIComponent(location.href) + '&t=' + encodeURIComponent(document.title);
                    }
                  },
                  ontimeout: function () {
                    if (button) {
                      button.textContent = {{silent}};
                    }
                  }
                });
              }

              const button = document.createElement('button');
              button.type = 'button';
              button.textContent = {{button}};
              button.style.cssText = 'position:fixed;right:18px;bottom:18px;z-index:2147483647;padding:9px 16px;border:0;border-radius:20px;' +
                'background:#01b4e4;color:#fff;font:600 14px/1.2 system-ui,sans-serif;cursor:pointer;box-shadow:0 2px 10px rgba(0,0,0,.35)';
              button.addEventListener('click', function () { send(button); });
              document.body.appendChild(button);
              // one film or series is sent by itself once (owner, 8. 11. 2026: its seasons and episodes are the same title)
              const title = location.pathname.match(/^\/(movie|tv)\/(\d+)/);
              const sent = 'universum-' + title[1] + '-' + title[2];
              let known = false;
              try { known = sessionStorage.getItem(sent) === '1'; } catch (e) { }
              if (AUTOMATIC && !known) {
                try { sessionStorage.setItem(sent, '1'); } catch (e) { }
                send(null);
              }
            })();

            """;
    }

    // a JavaScript string of a text (quotes and backslashes escaped)
    private static string Js(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>The button for the bookmarks bar ("Do UNIVERSUM"): it hands the page to UNIVERSUM by the address universum://.</summary>
    public const string Bookmarklet =
        "javascript:location.href='universum://tmdb?u='+encodeURIComponent(location.href)+'&t='+encodeURIComponent(document.title);void 0";
}
