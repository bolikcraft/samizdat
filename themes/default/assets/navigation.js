// Переход между страницами с деревом идёт без перезагрузки: страница приходит через fetch,
// в документ переходят <main>, заголовок окна и отметка текущей статьи. Дерево остаётся на месте
// вместе с раскрытием папок, фильтром и прокруткой.
// Сервер об этом не знает. Робот, браузер без Navigation API и страница без скриптов получают
// обычный переход и тот же html.
(function () {
  var tree = document.getElementById("nav-tree");
  var main = document.querySelector("main");
  if (!window.navigation || !tree || !main) return;

  // У этих адресов одно звено пути, но дерева на странице нет. Прочие страницы без дерева
  // (вложения, скачивание, гостевые ссылки) длиннее и отсеиваются по форме пути.
  var FULL_LOAD = ["/login", "/register", "/settings", "/background"];

  // Прокрутка каждой записи истории для «назад» и «вперёд». На широком экране прокручивается
  // <main>, на узком — окно.
  var scrolls = new Map();

  navigation.addEventListener("navigate", function (event) {
    var url = new URL(event.destination.url);
    if (!event.canIntercept || event.hashChange || event.formData
        || typeof event.downloadRequest === "string"
        || event.navigationType === "reload" || !isTreePage(url)) return;

    // До intercept текущая запись ещё прежняя.
    scrolls.set(navigation.currentEntry.key, { main: main.scrollTop, window: window.scrollY });
    var saved = event.navigationType === "traverse" ? scrolls.get(event.destination.key) : null;

    event.intercept({
      // Браузер прокручивает только окно, а на широком экране едет <main>.
      scroll: "manual",
      handler: function () {
        return load(url.href, event.signal).then(function (page) {
          if (!event.signal.aborted) return show(page, url, saved);
        }, function () {
          // Истёк вход, ошибка сервера или обрыв связи. Адрес уже новый: перезагрузка
          // показывает ответ сервера, как при обычном переходе.
          if (!event.signal.aborted) location.reload();
        });
      },
    });
  });

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

  function show(page, url, saved) {
    // Меняем детей, а не сам <main>: forms.js держит ссылку на элемент.
    main.replaceChildren.apply(main, Array.from(page.querySelector("main").childNodes));
    document.title = page.title;
    markCurrent(page);
    copySearch(page);
    // На узком экране дерево раскрывают переключателем. Обычная загрузка его сворачивает.
    var toggle = document.querySelector(".nav-switch");
    if (toggle) toggle.open = false;
    scroll(url, saved);
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

  function scroll(url, saved) {
    if (saved) {
      main.scrollTop = saved.main;
      window.scrollTo(0, saved.window);
      return;
    }
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
