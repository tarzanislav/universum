using System.Globalization;
using Universum.Core.Text;

namespace Universum.Core.Library.Tmdb;

/// <summary>
/// UNIVERSUM's script for the browser (owner, 8. 11. 2026; Tampermonkey or Violentmonkey run it; 1.3 – ČSFD and posters): on a page of a film or
/// series of TMDB it sends the page to the Zásobník TMDB by itself (UNIVERSUM takes it only while its automatic gathering is
/// switched on – 1.1), and shows the button "Do UNIVERSUM" (when UNIVERSUM
/// does not answer, the button starts it by the address universum://). It runs only on TMDB's film and series pages and
/// talks only to 127.0.0.1 with the key of this installation.
/// </summary>
public static class TmdbUserScript
{
    public const string Version = "1.3";

    public static string Text(int port, string key, bool automatic = true)
    {
        var button = Js(CoreText.T("＋ Do UNIVERSUM"));
        var added = Js(CoreText.T("✓ V zásobníku UNIVERSUM"));
        var refused = Js(CoreText.T("UNIVERSUM to neprijal"));
        var opening = Js(CoreText.T("Otváram UNIVERSUM…"));
        var silent = Js(CoreText.T("UNIVERSUM neodpovedá"));
        var poster = Js(CoreText.T("＋ plagát do UNIVERSUM"));
        var posterSent = Js(CoreText.T("✓ plagát poslaný"));
        var sending = Js(CoreText.T("posielam…"));
        return $$"""
            // ==UserScript==
            // @name         UNIVERSUM – Zásobník TMDB
            // @namespace    https://github.com/tarzanislav/universum
            // @version      {{Version}}
            // @description  Filmy, seriály a plagáty z TMDB a ČSFD do Zásobníka TMDB v UNIVERSUM. Posiela len na tento počítač (127.0.0.1).
            // @match        https://www.themoviedb.org/movie/*
            // @match        https://www.themoviedb.org/tv/*
            // @match        https://www.csfd.cz/film/*
            // @match        https://www.csfd.sk/film/*
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
              const CSFD = /(^|\.)csfd\.(cz|sk)$/.test(location.hostname);
              const title = CSFD ? location.pathname.match(/^\/film\/(\d+)/) : location.pathname.match(/^\/(movie|tv)\/(\d+)/);
              if (!title) {
                return;
              }

              // ČSFD links the title on IMDb: UNIVERSUM finds it on TMDB by it
              function imdb() {
                const link = document.querySelector('a[href*="imdb.com/title/tt"]');
                const id = link && link.href.match(/\/title\/(tt\d{5,10})/);
                return id ? id[1] : null;
              }

              function post(path, data, done) {
                if (CSFD) {
                  data.m = imdb();
                }

                GM_xmlhttpRequest({
                  method: 'POST',
                  url: 'http://127.0.0.1:' + PORT + path,
                  headers: { 'Content-Type': 'application/json', 'X-Universum-Key': KEY },
                  data: JSON.stringify(data),
                  timeout: 5000,
                  onload: function (answer) { done(answer.status); },
                  onerror: function () { done(0); },
                  ontimeout: function () { done(-1); }
                });
              }

              function send(button) {
                post(CSFD ? '/csfd' : '/tmdb', { u: location.href, t: document.title, a: !button }, function (status) {
                  if (!button) {
                    return;
                  }

                  if (status === 204 || status === 202) {
                    button.textContent = {{added}};
                  } else if (status === 0 && !CSFD) {
                    // UNIVERSUM does not run: the address universum:// starts it with the page
                    button.textContent = {{opening}};
                    location.href = 'universum://tmdb?u=' + encodeURIComponent(location.href) + '&t=' + encodeURIComponent(document.title);
                  } else if (status <= 0) {
                    button.textContent = {{silent}};
                  } else {
                    button.textContent = {{refused}} + ' (' + status + ')';
                  }
                });
              }

              const style = 'position:fixed;z-index:2147483647;border:0;border-radius:20px;color:#fff;cursor:pointer;' +
                'font:600 14px/1.2 system-ui,sans-serif;box-shadow:0 2px 10px rgba(0,0,0,.35);';
              const button = document.createElement('button');
              button.type = 'button';
              button.textContent = {{button}};
              button.style.cssText = style + 'right:18px;bottom:18px;padding:9px 16px;background:#01b4e4';
              button.addEventListener('click', function () { send(button); });
              document.body.appendChild(button);

              // one film or series is sent by itself once (owner, 8. 11. 2026: its seasons and episodes are the same title)
              const sent = 'universum-' + (CSFD ? 'csfd' : title[1]) + '-' + (CSFD ? title[1] : title[2]);
              let known = false;
              try { known = sessionStorage.getItem(sent) === '1'; } catch (e) { }
              if (AUTOMATIC && !known) {
                try { sessionStorage.setItem(sent, '1'); } catch (e) { }
                send(null);
              }

              // a poster under the mouse gets the button "plagát do UNIVERSUM" (owner, 8. 11. 2026: posters of ČSFD too)
              function tmdbPoster(address) {
                if (/\/images\/posters/.test(location.pathname)) {
                  return true;
                }

                const size = address.match(/\/t\/p\/w(\d+)_and_h(\d+)/);
                return !!size && size[2] / size[1] > 1.4 && size[2] / size[1] < 1.6;
              }

              function posterOf(img) {
                const addresses = [img.currentSrc, img.src, img.getAttribute('data-src')];
                const set = img.getAttribute('srcset') || img.getAttribute('data-srcset');
                if (set) {
                  set.split(',').forEach(function (part) { addresses.push(part.trim().split(/\s+/)[0]); });
                }

                for (const raw of addresses) {
                  if (!raw) {
                    continue;
                  }

                  const address = raw.indexOf('//') === 0 ? 'https:' + raw : raw;
                  if (CSFD ? /^https:\/\/image\.pmgstatic\.com\/(cache\/resized\/[a-z0-9]+\/)?files\/images\/film\/posters\//.test(address)
                           : /^https:\/\/(image\.tmdb\.org|media\.themoviedb\.org)\/t\/p\/[a-z0-9_]+\/[A-Za-z0-9]+\.(jpg|png|webp)$/.test(address) && tmdbPoster(address)) {
                    return address;
                  }
                }

                return null;
              }

              const pick = document.createElement('button');
              pick.type = 'button';
              pick.style.cssText = style + 'display:none;padding:6px 12px;font-size:12px;background:#0d253f';
              document.body.appendChild(pick);
              let shown = null;
              let address = null;
              document.addEventListener('mousemove', function (e) {
                const img = document.elementsFromPoint(e.clientX, e.clientY).find(function (el) { return el.tagName === 'IMG'; });
                const found = img && img.getBoundingClientRect().width >= 60 ? posterOf(img) : null;
                if (!found) {
                  pick.style.display = 'none';
                  shown = null;
                  return;
                }

                if (img !== shown) {
                  shown = img;
                  address = found;
                  pick.textContent = {{poster}};
                }

                const box = img.getBoundingClientRect();
                pick.style.left = (box.left + 6) + 'px';
                pick.style.top = (box.top + 6) + 'px';
                pick.style.display = 'block';
              }, { passive: true });
              window.addEventListener('scroll', function () { pick.style.display = 'none'; shown = null; }, { passive: true });
              pick.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                const chosen = address;
                pick.textContent = {{sending}};
                post('/poster', { u: location.href, t: document.title, i: chosen, a: false }, function (status) {
                  pick.textContent = status === 202 || status === 204 ? {{posterSent}} : status <= 0 ? {{silent}} : {{refused}} + ' (' + status + ')';
                });
              }, true);
            })();

            """;
    }

    // a JavaScript string of a text (quotes and backslashes escaped)
    private static string Js(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>The button for the bookmarks bar ("Do UNIVERSUM"): it hands the page to UNIVERSUM by the address universum://.</summary>
    public const string Bookmarklet =
        "javascript:location.href='universum://tmdb?u='+encodeURIComponent(location.href)+'&t='+encodeURIComponent(document.title);void 0";
}
