// Картинка статьи по щелчку открывается поверх страницы. Окно одно и стоит вне <main>:
// переход без перезагрузки меняет только детей <main>.
(function () {
  var dialog = document.createElement("dialog");
  dialog.className = "lightbox";
  var image = document.createElement("img");
  dialog.appendChild(image);
  document.body.appendChild(dialog);

  document.addEventListener("click", function (event) {
    var picture = event.target.closest("main article img");
    // Картинка в ссылке ведёт по ссылке.
    if (!picture || picture.closest("a[href]")) return;
    image.src = picture.src;
    image.alt = picture.alt;
    dialog.showModal();
  });

  dialog.addEventListener("click", function () { dialog.close(); });

  // layout.js ловит Esc на document и выключает режим чтения. Событие не отменяем:
  // отмена keydown не даёт самому окну закрыться.
  dialog.addEventListener("keydown", function (event) {
    if (event.key === "Escape") event.stopPropagation();
  });

  if (window.navigation) navigation.addEventListener("navigate", function () { dialog.close(); });
})();
