// Формы статьи — видимость и «Поделиться» — отправляются скриптом, и страница остаётся на месте:
// ответом приходит она же целиком, из ответа берутся свежие блоки и встают вместо старых.
// Сервер про это не знает и всегда отвечает редиректом на статью: без скрипта форма уходит
// обычным POST, и браузер рисует страницу заново.
(function () {
  var SWAPPED = "form.visibility-form, form.share-form";

  // Что забирается из ответа. «Поделиться» целиком: там меняется и ссылка, и набор форм.
  var BLOCKS = [".visibility-form", "details.share"];

  var main = document.querySelector("main");
  var canSwap = !!(main && window.fetch && window.DOMParser && window.FormData);

  document.addEventListener("submit", function (event) {
    var form = event.target;
    var button = submitter(form, event.submitter);
    if (button) button.classList.add("is-busy");

    // Прочие формы уходят как есть: страница перезагрузится, и отметка уйдёт вместе с ней.
    if (!canSwap || !form.matches(SWAPPED)) return;

    event.preventDefault();

    fetch(form.action, {
      method: "POST",
      body: new FormData(form),
      credentials: "same-origin",
      headers: { "X-Requested-With": "fetch" },
    }).then(function (response) {
      if (!response.ok) throw new Error("HTTP " + response.status);
      return response.text();
    }).then(function (html) {
      swap(html, button);
    }).catch(function () {
      // Отказ сервера или обрыв связи: отправляем форму по-настоящему, пусть браузер покажет
      // ответ как есть. Кнопки этих форм без name, отправка без них ничего не теряет.
      if (button) button.classList.remove("is-busy");
      form.submit();
    });
  });

  // Возврат «назад» отдаёт страницу из памяти браузера вместе с отметкой — снимаем её.
  window.addEventListener("pageshow", function () {
    document.querySelectorAll("button.is-busy").forEach(function (button) {
      button.classList.remove("is-busy");
    });
  });

  // Кнопку знает само событие, но форму отправляют и вводом в поле — тогда берём первую.
  function submitter(form, pressed) {
    if (pressed && pressed.form === form) return pressed;
    return form.querySelector("button[type=submit], button:not([type])");
  }

  function swap(html, button) {
    var fresh = new DOMParser().parseFromString(html, "text/html");

    BLOCKS.forEach(function (selector) {
      var live = main.querySelector(selector);
      var next = fresh.querySelector(selector);
      if (!live || !next) return;

      // Раскрытие «Поделиться» держит читатель, а в ответе блок всегда закрыт.
      if (live.open) next.open = true;

      var hadFocus = !!button && live.contains(button) && document.activeElement === button;
      live.replaceWith(next);
      // Нажатая кнопка ушла вместе с блоком, и фокус упал на страницу — возвращаем его на замену.
      if (hadFocus) {
        var again = next.querySelector("button");
        if (again) again.focus();
      }
    });

    markNav(fresh);
  }

  // Ссылка статьи в дереве бледнеет, пока статья закрыта. Дерево лежит вне обновляемых блоков,
  // поэтому метку переносим руками.
  function markNav(fresh) {
    var live = document.querySelector("#nav-tree a[aria-current=page]");
    var next = fresh.querySelector("#nav-tree a[aria-current=page]");
    if (live && next) live.className = next.className;
  }
})();
