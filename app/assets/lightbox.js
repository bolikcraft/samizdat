// Картинка статьи по щелчку открывается поверх страницы. Окно одно и стоит вне <main>:
// переход без перезагрузки меняет только детей <main>.
(function () {
  var dialog = document.createElement("dialog");
  dialog.className = "lightbox";
  var image = document.createElement("img");
  dialog.appendChild(image);
  document.body.appendChild(dialog);

  document.addEventListener("click", function (event) {
    var clicked = event.target.closest("main article img");
    // Картинка в ссылке ведёт по ссылке.
    if (!clicked || clicked.closest("a[href]")) return;
    image.src = clicked.src;
    image.alt = clicked.alt;
    dialog.showModal();
  });

  dialog.addEventListener("click", function () { dialog.close(); });

  // layout.js ловит Esc на document и выключает режим чтения. Ловим раньше, в фазе захвата:
  // после Tab фокус уходит из окна на <body>. Событие не отменяем, иначе окно не закроется.
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && dialog.open) event.stopPropagation();
  }, true);

  if (window.navigation) navigation.addEventListener("navigate", function () { dialog.close(); });
  // Без Navigation API «назад» уносит страницу в кэш браузера вместе с открытым окном.
  window.addEventListener("pagehide", function () { dialog.close(); });
})();
