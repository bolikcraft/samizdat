// Кнопки «свернуть боковик» и «режим чтения». Файл стоит в <head> без defer: отметки на <html>
// появляются до первой отрисовки, и свёрнутый боковик не мигает при загрузке.
// Кнопок в документе ещё нет, поэтому клики ловит document.
(function () {
  var STORAGE_KEY = "samizdat:sidebar";
  var root = document.documentElement;

  // По этой отметке тема показывает кнопки: без скрипта они ничего не делают.
  root.setAttribute("data-controls", "");
  try {
    if (localStorage.getItem(STORAGE_KEY) === "collapsed") root.setAttribute("data-sidebar", "collapsed");
  } catch (e) {
    // Приватный режим и т.п. — боковик просто развёрнут.
  }

  document.addEventListener("DOMContentLoaded", syncButtons);

  document.addEventListener("click", function (event) {
    if (event.target.closest("#sidebar-toggle")) toggleSidebar();
    else if (event.target.closest("#reading-toggle")) setReading(!root.hasAttribute("data-reading"));
  });

  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && root.hasAttribute("data-reading")) setReading(false);
  });

  function toggleSidebar() {
    var collapse = root.getAttribute("data-sidebar") !== "collapsed";
    try {
      if (collapse) localStorage.setItem(STORAGE_KEY, "collapsed");
      else localStorage.removeItem(STORAGE_KEY);
    } catch (e) {
      // Не запомнили — боковик свёрнут только до перезагрузки.
    }
    if (collapse) root.setAttribute("data-sidebar", "collapsed");
    else root.removeAttribute("data-sidebar");
    syncButtons();
  }

  // Режим чтения не запоминаем: после перезагрузки страница открывается как обычно.
  function setReading(on) {
    if (on) root.setAttribute("data-reading", "");
    else root.removeAttribute("data-reading");
    syncButtons();
  }

  function syncButtons() {
    var sidebar = document.getElementById("sidebar-toggle");
    var reading = document.getElementById("reading-toggle");
    if (sidebar) sidebar.setAttribute("aria-expanded", String(root.getAttribute("data-sidebar") !== "collapsed"));
    if (reading) reading.setAttribute("aria-pressed", String(root.hasAttribute("data-reading")));
  }
})();
