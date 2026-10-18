// Боковик работает и без этого файла: раскрытие через <details>, фильтр — только с ним.
(function () {
  var STORAGE_PREFIX = "samizdat:nav:";

  // Кнопка «Скопировать» стоит и в настройках, и в блоке «Поделиться» на статье. Обработчик
  // живёт тут, а не в странице: на статье встроенный <script> запрещён. Слушаем документ, а не
  // кнопки: блок «Поделиться» переписывается после отправки формы, и кнопка в нём каждый раз новая.
  document.addEventListener("click", function (event) {
    var button = event.target.closest(".copy-link");
    if (!button) return;

    navigator.clipboard.writeText(button.dataset.url).then(function () {
      button.textContent = button.dataset.copied || "Copied";
    });
  });

  document.querySelectorAll("#nav-tree details.nav-folder[data-path]").forEach(function (folder) {
    // Сервер ставит open только папкам с текущей статьёй: их не сворачиваем, иначе статью не видно.
    var hasCurrent = folder.open;
    var userToggled = false;

    try {
      var saved = localStorage.getItem(STORAGE_PREFIX + folder.dataset.path);
      if (saved !== null && !hasCurrent) folder.open = saved === "1";
    } catch (e) {
      // Приватный режим и т.п. — просто не запоминаем раскрытие.
    }

    // Событие toggle приходит позже и от любой смены open, в том числе от фильтра.
    // Поэтому запоминаем только раскрытие, которое сделал сам пользователь щелчком по заголовку.
    folder.querySelector(":scope > summary").addEventListener("click", function () {
      userToggled = true;
    });

    folder.addEventListener("toggle", function () {
      if (!userToggled) return;
      userToggled = false;
      try {
        localStorage.setItem(STORAGE_PREFIX + folder.dataset.path, folder.open ? "1" : "0");
      } catch (e) {
        // ignore
      }
    });
  });

  var filterInput = document.getElementById("nav-filter");
  var tree = document.getElementById("nav-tree");
  if (!filterInput || !tree) return;

  // Обход снизу вверх: узел виден, если у него самого есть совпадение или видим кто-то из детей.
  function applyFilter(node, query) {
    var visible = false;

    node.querySelectorAll(":scope > .nav-articles > li").forEach(function (item) {
      var match = item.textContent.toLowerCase().indexOf(query) !== -1;
      item.hidden = !match;
      visible = visible || match;
    });

    node.querySelectorAll(":scope > details.nav-folder").forEach(function (folder) {
      var childVisible = applyFilter(folder, query);
      folder.hidden = query !== "" && !childVisible;
      if (query !== "" && childVisible) folder.open = true;
      visible = visible || childVisible;
    });

    return visible;
  }

  // Раскрытие до начала фильтра: после очистки поля дерево возвращается к нему.
  var openBeforeFilter = null;

  filterInput.addEventListener("input", function () {
    var query = filterInput.value.trim().toLowerCase();
    var folders = tree.querySelectorAll("details.nav-folder");

    if (query !== "" && openBeforeFilter === null) {
      openBeforeFilter = Array.prototype.map.call(folders, function (folder) { return folder.open; });
    }

    applyFilter(tree, query);

    if (query === "" && openBeforeFilter !== null) {
      folders.forEach(function (folder, i) { folder.open = openBeforeFilter[i]; });
      openBeforeFilter = null;
    }
  });
})();
