// Переход между страницами с деревом идёт без перезагрузки: страница приходит через fetch,
// в документ переходят <main>, заголовок окна и отметка текущей статьи. Дерево остаётся на месте
// вместе с раскрытием папок, фильтром и прокруткой.
// Сервер об этом не знает. Робот, браузер без Navigation API и страница без скриптов получают
// обычный переход и тот же html.
(function () {
  var tree = document.getElementById("nav-tree");
  var main = document.querySelector("main");
  // Элемент с id="navigation" браузер тоже отдаёт как window.navigation. Проверяем, что это сам API.
  var hasApi = typeof window.Navigation === "function" && window.navigation instanceof window.Navigation;
  if (!hasApi || !tree || !main) return;

  // У этих адресов одно звено пути, но дерева на странице нет. Прочие страницы без дерева
  // (вложения, скачивание, гостевые ссылки) длиннее и отсеиваются по форме пути.
  var FULL_LOAD = ["/login", "/register", "/settings", "/background"];

  // Прокрутка каждой записи истории для «назад» и «вперёд». На широком экране прокручивается
  // <main>, на узком — окно.
  var scrolls = new Map();
  // Запись истории, чья страница сейчас в <main>. При быстрых щелчках currentEntry уже указывает
  // на запись, которая ещё грузится.
  var shownKey = navigation.currentEntry.key;

  // Заготовки страниц с наведения по адресу. Старые не берём: видимость статьи могли сменить в соседней вкладке.
  var PREFETCH_DELAY = 65;
  var PREFETCH_TTL = 10000;
  var prefetched = new Map();
  var timer = 0;

  navigation.addEventListener("navigate", function (event) {
    // Наведение перед щелчком не должно загрузить страницу второй раз после перехода.
    clearTimeout(timer);
    var url = new URL(event.destination.url);
    if (!event.canIntercept || event.hashChange || event.formData
        || typeof event.downloadRequest === "string"
        || event.navigationType === "reload" || !isTreePage(url)) return;

    scrolls.set(shownKey, { main: main.scrollTop, window: window.scrollY });
    var saved = event.navigationType === "traverse" ? scrolls.get(event.destination.key) : null;

    event.intercept({
      // Браузер прокручивает только окно, а на широком экране едет <main>.
      scroll: "manual",
      handler: function () {
        return (take(url) || load(url.href, event.signal)).then(function (page) {
          if (!event.signal.aborted) return show(page, url, saved, event.signal);
        }).catch(function () {
          // Истёк вход, ошибка сервера, обрыв связи или сбой подмены. Адрес уже новый:
          // перезагрузка показывает ответ сервера, как при обычном переходе.
          if (!event.signal.aborted) location.reload();
        });
      },
    });
  });

  // Курсор задержался на ссылке — начинаем загрузку. Щелчок потом берёт готовый ответ.
  document.addEventListener("pointerover", function (event) {
    var link = event.target.closest("a[href]");
    // У ссылки внутри svg href не строка.
    if (!(link instanceof HTMLAnchorElement)) return;
    // У битого адреса origin пустой, и new URL бросил бы исключение. Чужие сайты отсекаются тут же.
    if (link.origin !== location.origin) return;
    var url = new URL(link.href);
    if (!isTreePage(url) || pageHref(url) === pageHref(new URL(location.href))) return;
    clearTimeout(timer);
    timer = setTimeout(function () { prefetch(url); }, PREFETCH_DELAY);
  });

  document.addEventListener("pointerout", function (event) {
    if (event.target.closest("a[href]")) clearTimeout(timer);
  });

  // Форма меняет данные, и заготовка после неё могла устареть.
  document.addEventListener("submit", function () { prefetched.clear(); });

  function prefetch(url) {
    var href = pageHref(url);
    prefetched.forEach(function (entry, key) {
      if (Date.now() - entry.at > PREFETCH_TTL) {
        prefetched.delete(key);
      } else if (key !== href && !entry.done) {
        // Курсор ушёл с ссылки, а её загрузка делит канал с той, что нужна сейчас.
        entry.controller.abort();
        prefetched.delete(key);
      }
    });
    if (prefetched.has(href)) return;

    var entry = { at: Date.now(), controller: new AbortController(), done: false };
    entry.page = load(href, entry.controller.signal);
    // Отказ покажет сам переход. Без обработчика неиспользованный отказ попадает в консоль.
    entry.page.then(function () { entry.done = true; }, function () {});
    prefetched.set(href, entry);
  }

  // Документ заготовки годится один раз: show переносит его узлы в <main>.
  function take(url) {
    var href = pageHref(url);
    var entry = prefetched.get(href);
    prefetched.delete(href);
    if (!entry || Date.now() - entry.at > PREFETCH_TTL) return null;
    return entry.page;
  }

  function pageHref(url) {
    return url.origin + url.pathname + url.search;
  }

  function isTreePage(url) {
    var path = url.pathname.toLowerCase();
    if (url.origin !== location.origin || FULL_LOAD.indexOf(path) !== -1) return false;
    return path === "/" || /^\/[^/]+$/.test(path);
  }

  // Годится только страница с деревом без перенаправления. Страницы 403 и 404 тоже рисуются
  // с деревом и показываются так же.
  function load(href, signal) {
    return fetch(href, { credentials: "same-origin", signal: signal }).then(function (response) {
      var type = response.headers.get("Content-Type") || "";
      if (response.redirected || type.indexOf("text/html") !== 0) throw new Error("Not a tree page");
      return response.text();
    }).then(function (html) {
      var page = new DOMParser().parseFromString(html, "text/html");
      if (!page.getElementById("nav-tree") || !page.querySelector("main")) throw new Error("Not a tree page");
      return page;
    });
  }

  // Прокрутка идёт внутри смены кадра: иначе новый текст на миг встанет на старой высоте.
  function show(page, url, saved, signal) {
    var update = function () {
      // Смена кадра начинается не сразу. Если за это время начался другой переход, страницу не ставим.
      if (!signal.aborted) return swap(page, url, saved, signal);
    };
    if (!document.startViewTransition) return update();
    var transition = document.startViewTransition(update);
    // Новая смена кадра пропускает прежнюю, и её ready отклоняется. Это не ошибка.
    transition.ready.catch(function () {});
    return transition.updateCallbackDone;
  }

  function swap(page, url, saved, signal) {
    // При «назад» читатель возвращается на своё место, а поле с autofocus утянуло бы прокрутку к себе.
    if (saved) {
      page.querySelectorAll("main [autofocus]").forEach(function (field) { field.removeAttribute("autofocus"); });
    }
    // Меняем детей, а не сам <main>: forms.js держит ссылку на элемент.
    main.replaceChildren.apply(main, Array.from(page.querySelector("main").childNodes));
    // Схемы в новом <main> рисует diagrams.js. Файл мог ещё не загрузиться.
    // Тогда window.drawDiagrams бывает элементом статьи с id="drawDiagrams".
    var diagrams = typeof window.drawDiagrams === "function" ? window.drawDiagrams() : Promise.resolve();
    shownKey = navigation.currentEntry.key;
    document.title = page.title;
    markCurrent(page);
    copySearch(page);
    // На узком экране дерево раскрывают переключателем. Обычная загрузка его сворачивает.
    var toggle = document.querySelector(".nav-switch");
    if (toggle) toggle.open = false;
    // Пока ждём картинки и схемы, читатель мог уйти дальше: тогда прокрутка этой записи уже не нужна.
    if (saved) return heightsReady(diagrams).then(function () { if (!signal.aborted) restore(saved); });
    scrollToStart(url);
  }

  // Дерево не заменяем. Переносим только отметку текущей статьи и раскрываем папки над ней,
  // как это делает сервер.
  function markCurrent(page) {
    var old = tree.querySelector("a[aria-current=page]");
    if (old) old.removeAttribute("aria-current");

    var fresh = page.querySelector("#nav-tree a[aria-current=page]");
    if (!fresh) return;
    var link = tree.querySelector('a[href="' + CSS.escape(fresh.getAttribute("href")) + '"]');
    if (!link) return;

    link.setAttribute("aria-current", "page");
    // Класс несёт видимость статьи, она могла смениться.
    link.className = fresh.className;
    for (var folder = link.closest("details.nav-folder"); folder;
         folder = folder.parentElement.closest("details.nav-folder")) {
      folder.open = true;
    }
  }

  // Поле поиска в шапке лежит вне <main>. После ухода со страницы поиска запрос в нём не нужен.
  function copySearch(page) {
    var live = document.querySelector(".search input[name=q]");
    var fresh = page.querySelector(".search input[name=q]");
    if (live && fresh) live.value = fresh.value;
  }

  // Картинка без размеров получает высоту только после декодирования, схема — после рисования.
  // До этого высота текста другая, и возвращённая прокрутка встаёт не туда. Ждём, но не дольше полсекунды.
  function heightsReady(diagrams) {
    var decoded = Promise.all(Array.from(main.querySelectorAll("img"), function (img) {
      return img.decode().catch(function () {});
    }));
    return Promise.race([Promise.all([decoded, diagrams]),
                         new Promise(function (done) { setTimeout(done, 500); })]);
  }

  function restore(saved) {
    main.scrollTop = saved.main;
    window.scrollTo(0, saved.window);
  }

  function scrollToStart(url) {
    var target = fragment(url);
    if (target) {
      target.scrollIntoView();
      return;
    }
    main.scrollTop = 0;
    window.scrollTo(0, 0);
  }

  function fragment(url) {
    if (!url.hash) return null;
    var id = url.hash.slice(1);
    try {
      id = decodeURIComponent(id);
    } catch (e) {
      // Битая кодировка в адресе: ищем id как есть.
    }
    return document.getElementById(id);
  }
})();
