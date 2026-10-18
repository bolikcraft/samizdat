// Страница настроек. Разделы переключаются якорем и без скрипта, скрипт добавляет удобства:
// подсветку пункта, переход со старых якорей, сохранение галок без перезагрузки,
// отправку списков сразу при выборе и закрытие меню «⋯».
(function () {
  var links = document.querySelectorAll(".settings-nav a");
  var navSwitch = document.querySelector(".sidebar .nav-switch");
  var canSwap = !!(window.fetch && window.DOMParser && window.FormData);

  // Адреса из закладок и старых писем ведут на разделы, которых больше нет.
  var MOVED = {
    "#security": "#profile", "#language": "#profile",
    "#appearance": "#general",
    "#people": "#users", "#signup": "#users",
    "#articles": "#publishing", "#links": "#publishing",
    "#tokens": "#api",
  };

  function follow() {
    var target = MOVED[location.hash];
    // replace, а не replaceState: только переход по якорю обновляет :target.
    if (target) { location.replace(target); return; }
    mark();
  }

  // :target показывает панель, но до пункта в боковике не дотягивается.
  function mark() {
    if (links.length === 0) return;
    var current = location.hash || links[0].getAttribute("href");
    links.forEach(function (link) {
      if (link.getAttribute("href") === current) link.setAttribute("aria-current", "page");
      else link.removeAttribute("aria-current");
    });
  }

  // Кнопку «Сохранить» у форм без кнопки видно только без скрипта.
  function hideButtons(root) {
    root.querySelectorAll("form[data-autosave] button[type=submit], form[data-submit-on-change] button[type=submit]")
      .forEach(function (button) { button.hidden = true; });
  }

  follow();
  window.addEventListener("hashchange", follow);
  hideButtons(document);

  // На узком экране список разделов свёрнут: после выбора закрываем его, иначе раздел ниже экрана.
  links.forEach(function (link) {
    link.addEventListener("click", function () { if (navSwitch) navSwitch.open = false; });
  });

  document.addEventListener("change", function (event) {
    var form = event.target.form;
    if (!form) return;

    if (form.hasAttribute("data-submit-on-change")) {
      // Язык, тема и схема меняют вид всей страницы: её проще получить заново.
      form.submit();
      return;
    }
    if (form.hasAttribute("data-autosave")) {
      if (canSwap) save(form); else form.submit();
    }
  });

  // Сохранения в пути по имени формы: "busy" или "again", если галку сменили до ответа.
  // pointer-events не держит клавиатуру, поэтому второй запрос не шлём, а ждём первый.
  var saving = {};

  function liveForm(name) {
    return document.querySelector('form[data-autosave="' + name + '"]');
  }

  // Обработчик делегирован: форма после сохранения подменяется свежей из ответа.
  function save(form) {
    var name = form.getAttribute("data-autosave");
    if (saving[name]) { saving[name] = "again"; return; }
    saving[name] = "busy";
    form.classList.add("is-saving");

    fetch(form.action, {
      method: "POST",
      body: new FormData(form),
      credentials: "same-origin",
      headers: { "X-Requested-With": "fetch" },
    }).then(function (response) {
      if (!response.ok) throw new Error("HTTP " + response.status);
      return response.text();
    }).then(function (html) {
      var live = liveForm(name);
      var again = saving[name] === "again";
      delete saving[name];
      // Ответ уже устарел: на экране новое состояние, его и отправляем.
      if (again) { save(live); return; }

      var fresh = new DOMParser().parseFromString(html, "text/html");
      var next = fresh.querySelector('form[data-autosave="' + name + '"]');
      if (!next) throw new Error("form " + name + " is missing");

      var focused = live.contains(document.activeElement) ? document.activeElement.name : null;
      hideButtons(next);
      live.replaceWith(next);
      if (focused) {
        var control = next.querySelector('[name="' + focused + '"]');
        if (control) control.focus();
      }
      showToast(fresh.querySelector(".toast"));
    }).catch(function () {
      // Отказ сервера или обрыв: отправляем форму по-настоящему, браузер покажет ответ как есть.
      delete saving[name];
      liveForm(name).submit();
    });
  }

  // Возврат «назад» после запасной отправки отдаёт страницу из памяти вместе с отметкой.
  window.addEventListener("pageshow", function () {
    document.querySelectorAll("form.is-saving").forEach(function (form) {
      form.classList.remove("is-saving");
    });
  });

  // Новый элемент, а не старый с новым текстом: так анимация появления начинается заново.
  function showToast(toast) {
    var old = document.querySelector(".toast");
    if (old) old.remove();
    if (!toast) return;
    var panes = document.querySelector(".settings-panes");
    panes.parentNode.insertBefore(document.importNode(toast, true), panes);
  }

  // Открытое меню «⋯» одно: новое закрывает прежнее. Щелчок мимо меню и Esc закрывают его.
  document.addEventListener("toggle", function (event) {
    var menu = event.target;
    if (!menu.matches || !menu.matches("details.row-menu") || !menu.open) return;
    document.querySelectorAll("details.row-menu[open]").forEach(function (other) {
      if (other !== menu) other.open = false;
    });
  }, true);

  document.addEventListener("click", function (event) {
    document.querySelectorAll("details.row-menu[open]").forEach(function (menu) {
      if (!menu.contains(event.target)) menu.open = false;
    });
  });

  document.addEventListener("keydown", function (event) {
    if (event.key !== "Escape") return;
    document.querySelectorAll("details.row-menu[open]").forEach(function (menu) {
      menu.open = false;
      menu.querySelector("summary").focus();
    });
  });

  // Выбранный файл фона отправляется сам. Кнопка «Загрузить» остаётся для страницы без скриптов.
  var upload = document.querySelector(".bg-upload");
  if (upload) {
    upload.querySelector(".bg-upload-go").hidden = true;
    upload.querySelector("input[type=file]").addEventListener("change", function () {
      if (this.files.length > 0) upload.submit();
    });
  }
})();
