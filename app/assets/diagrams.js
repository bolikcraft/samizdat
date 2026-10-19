// Блок pre.mermaid в статье становится схемой. Сам mermaid весит около 3,5 МБ,
// поэтому он грузится, только когда на странице есть схема.
(function () {
  var BLOCKS = "main pre.mermaid:not([data-diagram])";
  var loading = null;
  // Поиски идут по очереди: иначе блок, который ещё рисуется, взял бы и следующий поиск.
  var queue = Promise.resolve();
  var count = 0;

  function run() {
    queue = queue.then(drawAll).catch(function () {
      // mermaid не загрузился: блоки остаются кодом.
    });
  }

  function drawAll() {
    var blocks = Array.from(document.querySelectorAll(BLOCKS));
    if (blocks.length === 0) return Promise.resolve();
    return load().then(function (mermaid) {
      // Без suppressErrorRendering mermaid оставляет картинку ошибки в конце <body>.
      mermaid.initialize({
        startOnLoad: false, securityLevel: "strict", theme: isDark() ? "dark" : "default",
        suppressErrorRendering: true,
      });
      // По одному: ошибка одной схемы не мешает другим.
      return blocks.reduce(function (previous, block) {
        return previous.then(function () { return draw(mermaid, block); });
      }, Promise.resolve());
    });
  }

  function load() {
    if (!loading) {
      loading = new Promise(function (resolve, reject) {
        var script = document.createElement("script");
        script.src = "/_app/vendor/mermaid.min.js";
        script.addEventListener("load", function () { resolve(window.mermaid); });
        script.addEventListener("error", reject);
        document.head.appendChild(script);
      });
    }
    return loading;
  }

  // Блок меняется только после удачного рисования. При ошибке в нём так и остаётся исходный текст.
  function draw(mermaid, block) {
    var source = block.textContent;
    count += 1;
    // Заглавная буква в id: Markdig пишет id заголовков строчными, а mermaid перед рисованием
    // удаляет со страницы элемент с тем же id.
    return mermaid.render("Diagram" + count, source).then(function (result) {
      block.innerHTML = result.svg;
      naturalSize(block.querySelector("svg"));
      block.setAttribute("data-diagram", "done");
    }, function () {
      block.setAttribute("data-diagram", "error");
    });
  }

  // mermaid ужимает схему до ширины блока, и у широкой схемы текст не прочесть.
  // useMaxWidth в initialize не годится: этот ключ свой у каждого вида схемы.
  function naturalSize(svg) {
    if (!svg || !svg.style.maxWidth) return;
    svg.setAttribute("width", svg.style.maxWidth);
    svg.style.maxWidth = "";
  }

  // Как в style.css темы: тёмная схема выбрана явно или сайт идёт за тёмной системой.
  function isDark() {
    var scheme = document.documentElement.getAttribute("data-color-scheme");
    return scheme === "dark"
      || (scheme !== "light" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  }

  run();
  // Переход без перезагрузки меняет только детей <main>, а этот файл заново не выполняется.
  if (window.navigation) navigation.addEventListener("navigatesuccess", run);
})();
