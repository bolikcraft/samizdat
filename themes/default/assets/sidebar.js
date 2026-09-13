// Боковик работает и без этого файла: раскрытие через <details>, фильтр — только с ним.
(function () {
  var STORAGE_PREFIX = "samizdat:nav:";

  document.querySelectorAll("#nav-tree details.nav-folder[data-path]").forEach(function (folder) {
    try {
      var saved = localStorage.getItem(STORAGE_PREFIX + folder.dataset.path);
      if (saved !== null) folder.open = saved === "1";
    } catch (e) {
      // Приватный режим и т.п. — просто не запоминаем раскрытие.
    }

    folder.addEventListener("toggle", function () {
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

  filterInput.addEventListener("input", function () {
    applyFilter(tree, filterInput.value.trim().toLowerCase());
  });
})();
