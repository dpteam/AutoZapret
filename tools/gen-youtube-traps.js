// Генератор hosts-фикса «YouTube — ловушки российских кэшей».
//
//   node tools/gen-youtube-traps.js
//
// Что это и зачем. РКН подменил кэширующие серверы YouTube, стоящие в России:
// имена остались прежними, а отдают они медленно или не отдают вовсе. Браузер
// берёт сервер из списка, который вернул YouTube, попадает на ловушку — и
// видео висит, хотя страница и картинки открываются.
//
// Фикс привязывает известные имена-ловушки к 0.0.0.0 (в никуда). Тогда YouTube
// выбирает следующий сервер из своего списка, и если с зарубежными соединение
// есть — видео идёт. Если нет, поможет только VPN/WARP: блокировка ловушек не
// создаёт маршрут там, где его нет.
//
// Почему имён так много. Ловушки у каждого провайдера свои, и списки собирали
// разные люди. Здесь объединение: лишнее имя, которого у вашего провайдера
// нет, ни на что не влияет — оно и так не резолвится.
//
// Что НЕ включено и почему:
//   * NOT_RUSSIAN — восемь имён, которые проверили и нашли рабочими
//     зарубежными (Брюссель, Торонто, Маунтин-Вью). Блокировать хороший
//     сервер значит отнимать у YouTube рабочий вариант.
//   * UNVERIFIED — три имени, которые не заресолвились при проверке. Догадка
//     «наверное тоже не в России» — не основание блокировать.
//
// Источник: github.com/Flowseal/zapret-discord-youtube/issues/18271
// (комментарии ekungurov, d0ntee, anrej0705 + список для Дом.ру).
//
// Второй источник, который просили изучить, — discussions/251 «Обход YouTube».
// Там hosts-блоков нет: 48 имён кэшей упоминаются в `alt-svc-mapping-for-testing`,
// это конфиг браузера для HTTP/3, а не блокировка. Проверено: ни одной строки
// «IP + имя» в нём нет, поэтому из обсуждения сюда ничего не попало.
const fs = require("fs");
const path = require("path");

// Имена-ловушки, собранные из списков в issue (Дом.ру, Мегафон, МТС, прочие).
// Хранятся как есть, без адреса: адрес у всех один — блокировка.
const TRAPS = [
  // --- Дом.ру (список из issue и от заказчика)
  "rr1---sn-xguxaxjvh-2x1e.googlevideo.com",
  "rr2---sn-xguxaxjvh-2x1e.googlevideo.com",
  "rr3---sn-xguxaxjvh-2x1e.googlevideo.com",
  "rr1---sn-xguxaxjvh-304e.googlevideo.com",
  "rr2---sn-xguxaxjvh-304e.googlevideo.com",
  "rr3---sn-xguxaxjvh-304e.googlevideo.com",
  "rr4---sn-xguxaxjvh-304e.googlevideo.com",
  "rr1---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr2---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr3---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr4---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr5---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr6---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr7---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr8---sn-xguxaxjvh-8v1e.googlevideo.com",
  "rr1---sn-xguxaxjvh-8v1s.googlevideo.com",
  "rr2---sn-xguxaxjvh-8v1s.googlevideo.com",
  "rr3---sn-xguxaxjvh-8v1s.googlevideo.com",
  "rr4---sn-xguxaxjvh-8v1s.googlevideo.com",
  "rr1---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr2---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr3---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr4---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr5---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr6---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr7---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr8---sn-xguxaxjvh-bvwe.googlevideo.com",
  "rr1---sn-xguxaxjvh-bvws.googlevideo.com",
  "rr2---sn-xguxaxjvh-bvws.googlevideo.com",
  "rr3---sn-xguxaxjvh-bvws.googlevideo.com",
  "rr4---sn-xguxaxjvh-bvws.googlevideo.com",
  "rr1---sn-xguxaxjvh-gufl.googlevideo.com",
  "rr2---sn-xguxaxjvh-gufl.googlevideo.com",
  "rr3---sn-xguxaxjvh-gufl.googlevideo.com",
  "rr4---sn-xguxaxjvh-gufl.googlevideo.com",
  "rr1---sn-xguxaxjvh-uwhs.googlevideo.com",
  "rr2---sn-xguxaxjvh-uwhs.googlevideo.com",
  "rr3---sn-xguxaxjvh-uwhs.googlevideo.com",
  "rr4---sn-xguxaxjvh-uwhs.googlevideo.com",

  // --- Мегафон / МТС (ekungurov: только проверенные российские)
  "r1---sn-hxb54vo-3046.googlevideo.com",
  "r2---sn-hxb54vo-3046.googlevideo.com",
  "r3---sn-hxb54vo-3046.googlevideo.com",
  "r4---sn-hxb54vo-3046.googlevideo.com",
  "rr1---sn-hxb54vo-045e.googlevideo.com",
  "rr2---sn-hxb54vo-045e.googlevideo.com",
  "rr3---sn-hxb54vo-045e.googlevideo.com",
  "rr4---sn-hxb54vo-045e.googlevideo.com",
  "rr1---sn-hxb54vo-304d.googlevideo.com",
  "rr2---sn-hxb54vo-304d.googlevideo.com",
  "rr3---sn-hxb54vo-304d.googlevideo.com",
  "rr4---sn-hxb54vo-304d.googlevideo.com",
  "rr1---sn-hxb54vo-304k.googlevideo.com",
  "rr2---sn-hxb54vo-304k.googlevideo.com",
  "rr3---sn-hxb54vo-304k.googlevideo.com",
  "rr4---sn-hxb54vo-304k.googlevideo.com",
  "rr1---sn-hxb54vo-3046.googlevideo.com",
  "rr2---sn-hxb54vo-3046.googlevideo.com",
  "rr3---sn-hxb54vo-3046.googlevideo.com",
  "rr4---sn-hxb54vo-3046.googlevideo.com",
  "rr1---sn-hxb54vo-304z.googlevideo.com",
  "rr2---sn-hxb54vo-304z.googlevideo.com",
  "rr3---sn-hxb54vo-304z.googlevideo.com",
  "rr4---sn-hxb54vo-304z.googlevideo.com",
  "rr1---sn-hxb54vo-304s.googlevideo.com",
  "rr2---sn-hxb54vo-304s.googlevideo.com",
  "rr3---sn-hxb54vo-304s.googlevideo.com",
  "rr4---sn-hxb54vo-304s.googlevideo.com",
  "rr1---sn-hxb54vo-bvwl.googlevideo.com",
  "rr2---sn-hxb54vo-bvwl.googlevideo.com",
  "rr3---sn-hxb54vo-bvwl.googlevideo.com",
  "rr4---sn-hxb54vo-bvwl.googlevideo.com",
  "rr1---sn-g5njvh-n8vl.googlevideo.com",
  "rr2---sn-g5njvh-n8vl.googlevideo.com",
  "rr3---sn-g5njvh-n8vl.googlevideo.com",
  "rr4---sn-g5njvh-n8vl.googlevideo.com",
  "rr1---sn-jvhnu5g-c35z.googlevideo.com",
  "rr2---sn-jvhnu5g-c35z.googlevideo.com",
  "rr3---sn-jvhnu5g-c35z.googlevideo.com",
  "rr4---sn-jvhnu5g-c35z.googlevideo.com",
  "rr5---sn-jvhnu5g-c35z.googlevideo.com",
  "rr6---sn-jvhnu5g-c35z.googlevideo.com",
  "rr7---sn-jvhnu5g-c35z.googlevideo.com",
  "rr8---sn-jvhnu5g-c35z.googlevideo.com",
  "rr1---sn-jvhnu5g-n8ve7.googlevideo.com",
  "rr2---sn-jvhnu5g-n8ve7.googlevideo.com",
  "rr3---sn-jvhnu5g-n8ve7.googlevideo.com",
  "rr4---sn-jvhnu5g-n8ve7.googlevideo.com",
  "rr1---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr2---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr3---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr4---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr5---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr6---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr7---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr8---sn-jvhnu5g-n8vk.googlevideo.com",
  "rr1---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr2---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr3---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr4---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr5---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr6---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr7---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr8---sn-jvhnu5g-n8vr.googlevideo.com",
  "rr1---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr2---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr3---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr4---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr5---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr6---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr7---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr8---sn-jvhnu5g-n8vy.googlevideo.com",
  "rr1---sn-jvhnu5g-p3ue.googlevideo.com",
  "rr2---sn-jvhnu5g-p3ue.googlevideo.com",
  "rr3---sn-jvhnu5g-p3ue.googlevideo.com",
  "rr4---sn-jvhnu5g-p3ue.googlevideo.com",
  "rr5---sn-jvhnu5g-p3ue.googlevideo.com",
  "r1---sn-oxuctoxu-n8vl.googlevideo.com",
  "r2---sn-oxuctoxu-n8vl.googlevideo.com",
  "r3---sn-oxuctoxu-n8vl.googlevideo.com",
  "r4---sn-oxuctoxu-n8vl.googlevideo.com",
  "r5---sn-oxuctoxu-n8vl.googlevideo.com",
  "r6---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr1---sn-oxuctoxu-axqe.googlevideo.com",
  "rr2---sn-oxuctoxu-axqe.googlevideo.com",
  "rr3---sn-oxuctoxu-axqe.googlevideo.com",
  "rr4---sn-oxuctoxu-axqe.googlevideo.com",
  "rr1---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr2---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr3---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr4---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr5---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr6---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr1---sn-oxuctoxu-xbvl.googlevideo.com",
  "rr2---sn-oxuctoxu-xbvl.googlevideo.com",
  "rr3---sn-oxuctoxu-xbvl.googlevideo.com",
  "rr4---sn-oxuctoxu-xbvl.googlevideo.com",
  "rr5---sn-oxuctoxu-xbvl.googlevideo.com",
  "rr6---sn-oxuctoxu-xbvl.googlevideo.com",
  "rr1---sn-4g5e6ns6.googlevideo.com",
  "rr2---sn-4g5e6ns6.googlevideo.com",
  "rr3---sn-4g5e6ns6.googlevideo.com",
  "rr4---sn-4g5e6ns6.googlevideo.com",
  "rr5---sn-4g5e6ns6.googlevideo.com",
  "rr1---sn-4g5e6nsr.googlevideo.com",
  "rr2---sn-4g5e6nsr.googlevideo.com",
  "rr3---sn-4g5e6nsr.googlevideo.com",
  "rr4---sn-4g5e6nsr.googlevideo.com",
  "rr5---sn-4g5e6nsr.googlevideo.com",
  "rr1---sn-4g5e6nsz.googlevideo.com",
  "rr2---sn-4g5e6nsz.googlevideo.com",
  "rr3---sn-4g5e6nsz.googlevideo.com",
  "rr4---sn-4g5e6nsz.googlevideo.com",
  "rr5---sn-4g5e6nsz.googlevideo.com",
  "rr1---sn-4g5e6nzs.googlevideo.com",
  "rr2---sn-4g5e6nzs.googlevideo.com",
  "rr3---sn-4g5e6nzs.googlevideo.com",
  "rr4---sn-4g5e6nzs.googlevideo.com",
  "rr5---sn-4g5e6nzs.googlevideo.com",
  "rr1---sn-4g5edndl.googlevideo.com",
  "rr2---sn-4g5edndl.googlevideo.com",
  "rr3---sn-4g5edndl.googlevideo.com",
  "rr4---sn-4g5edndl.googlevideo.com",
  "rr5---sn-4g5edndl.googlevideo.com",
  "rr1---sn-4g5ednsz.googlevideo.com",
  "rr2---sn-4g5ednsz.googlevideo.com",
  "rr3---sn-4g5ednsz.googlevideo.com",
  "rr4---sn-4g5ednsz.googlevideo.com",
  "rr5---sn-4g5ednsz.googlevideo.com",
  "rr1---sn-4g5lznl6.googlevideo.com",
  "rr2---sn-4g5lznl6.googlevideo.com",
  "rr3---sn-4g5lznl6.googlevideo.com",
  "rr4---sn-4g5lznl6.googlevideo.com",
  "rr5---sn-4g5lznl6.googlevideo.com",
  "rr1---sn-4g5lznl7.googlevideo.com",
  "rr2---sn-4g5lznl7.googlevideo.com",
  "rr3---sn-4g5lznl7.googlevideo.com",
  "rr4---sn-4g5lznl7.googlevideo.com",
  "rr5---sn-4g5lznl7.googlevideo.com",
  "rr1---sn-4g5lznlz.googlevideo.com",
  "rr2---sn-4g5lznlz.googlevideo.com",
  "rr3---sn-4g5lznlz.googlevideo.com",
  "rr4---sn-4g5lznlz.googlevideo.com",
  "rr5---sn-4g5lznlz.googlevideo.com",

  // --- Мегафон Московская область (anrej0705) и старый домен c.youtube.com
  "rr1---sn-1giz7n7l.googlevideo.com",
  "rr2---sn-1giz7n7l.googlevideo.com",
  "rr3---sn-1giz7n7l.googlevideo.com",
  "rr4---sn-1giz7n7l.googlevideo.com",
  "rr5---sn-1giz7n7l.googlevideo.com",
  "rr1---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr7---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr8---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr1---sn-oj1gi5-54.c.youtube.com",
  "rr2---sn-oj1gi5-54.c.youtube.com",
  "rr3---sn-oj1gi5-54.c.youtube.com",
  "rr4---sn-oj1gi5-54.c.youtube.com",
  "rr5---sn-oj1gi5-54.c.youtube.com",
  "rr1---sn-f5f7kn7e.c.youtube.com",
  "rr2---sn-f5f7kn7e.c.youtube.com",
  "rr3---sn-f5f7kn7e.c.youtube.com",
  "rr4---sn-f5f7kn7e.c.youtube.com",
  "rr5---sn-f5f7kn7e.c.youtube.com",
  "rr1---sn-f5f7lnly.c.youtube.com",
  "rr2---sn-f5f7lnly.c.youtube.com",
  "rr3---sn-f5f7lnly.c.youtube.com",
  "rr4---sn-f5f7lnly.c.youtube.com",
  "rr5---sn-f5f7lnly.c.youtube.com",
  "rr1---sn-2of5f5-5i.c.youtube.com",
  "rr2---sn-2of5f5-5i.c.youtube.com",
  "rr3---sn-2of5f5-5i.c.youtube.com",
  "rr4---sn-2of5f5-5i.c.youtube.com",
  "rr5---sn-2of5f5-5i.c.youtube.com",

  // --- Ростелеком (в issue он записан ОДНОЙ строкой через пробелы,
  //     поэтому построчное извлечение его пропустило — берём руками)
  "rr1---sn-gvnuxaxjvh-n8ml.googlevideo.com",
  "rr2---sn-gvnuxaxjvh-n8ml.googlevideo.com",
  "rr3---sn-gvnuxaxjvh-n8ml.googlevideo.com",
  "rr4---sn-gvnuxaxjvh-n8ml.googlevideo.com",
  "rr5---sn-gvnuxaxjvh-n8ml.googlevideo.com",
  "rr6---sn-gvnuxaxjvh-n8ml.googlevideo.com",
  "rr7---sn-gvnuxaxjvh-n8ml.googlevideo.com",

  // --- прочие из тредов
  "rr1---sn-hxb54vo-3046.googlevideo.com",
  "rr2---sn-hxb54vo-3046.googlevideo.com",
  "rr3---sn-hxb54vo-3046.googlevideo.com",
  "rr4---sn-hxb54vo-3046.googlevideo.com"
];

// Проверено как рабочие зарубежные: не блокируем.
const NOT_RUSSIAN = [
  "rr3---sn-aj4g55-5t.googlevideo.com",   // 173.194.19.67   Брюссель
  "rr1---sn-aj5go5-5i.googlevideo.com",   // 173.194.160.161 Брюссель
  "rr2---sn-aj5goc-5j.googlevideo.com",   // 173.194.164.210 Брюссель
  "rr3---sn-u14g55-5m.googlevideo.com",   // 173.194.165.3   Брюссель
  "rr3---sn-u14g55-cf.googlevideo.com",   // 74.125.111.99   Торонто
  "rr1---sn-q4fzen7e.googlevideo.com",    // 173.194.57.230  Брюссель
  "rr5---sn-q4fzen7l.googlevideo.com",    // 173.194.140.10  Маунтин-Вью
  "rr5---sn-4g5ednky.googlevideo.com"     // 74.125.173.170  Торонто
];

// Убраны 27 имён, которых не существует.
//
// Авторы списков в issue дописали rr1..rr10 «на всякий случай», но у этих
// семейств существуют только rr1..rr5 (у sn-oxuctoxu-n8vl — rr1..rr8).
// Проверено резолвом каждого номера от rr1 до rr12: граница чёткая.
//
// Пять из шести таких семейств оказались зарубежными целиком (DE/PL/CH),
// то есть блок был выдуман не только в номерах, но и в смысле.
//
// Оставлять их «на всякий случай» нельзя: они не резолвятся, блокировать
// нечего, а в списке выглядят как настоящие ловушки и мешают считать.
const PHANTOM = [
  "rr6---sn-1giz7n7l.googlevideo.com",
  "rr7---sn-1giz7n7l.googlevideo.com",
  "rr8---sn-1giz7n7l.googlevideo.com",
  "rr9---sn-1giz7n7l.googlevideo.com",
  "rr10---sn-1giz7n7l.googlevideo.com",

  "rr6---sn-2of5f5-5i.c.youtube.com",
  "rr7---sn-2of5f5-5i.c.youtube.com",
  "rr8---sn-2of5f5-5i.c.youtube.com",
  "rr9---sn-2of5f5-5i.c.youtube.com",
  "rr10---sn-2of5f5-5i.c.youtube.com",

  "rr6---sn-f5f7kn7e.c.youtube.com",
  "rr7---sn-f5f7kn7e.c.youtube.com",
  "rr8---sn-f5f7kn7e.c.youtube.com",
  "rr9---sn-f5f7kn7e.c.youtube.com",
  "rr10---sn-f5f7kn7e.c.youtube.com",

  "rr6---sn-f5f7lnly.c.youtube.com",
  "rr7---sn-f5f7lnly.c.youtube.com",
  "rr8---sn-f5f7lnly.c.youtube.com",
  "rr9---sn-f5f7lnly.c.youtube.com",
  "rr10---sn-f5f7lnly.c.youtube.com",

  "rr6---sn-oj1gi5-54.c.youtube.com",
  "rr7---sn-oj1gi5-54.c.youtube.com",
  "rr8---sn-oj1gi5-54.c.youtube.com",
  "rr9---sn-oj1gi5-54.c.youtube.com",
  "rr10---sn-oj1gi5-54.c.youtube.com",

  "rr9---sn-oxuctoxu-n8vl.googlevideo.com",
  "rr10---sn-oxuctoxu-n8vl.googlevideo.com"
];

// Проверено: ведут на РАБОЧИЕ зарубежные серверы Google (AS15169).
//
// Как проверено: каждое имя резолвилось через 1.1.1.1 и через Яндекс (77.88.8.8),
// адрес смотрелся в ipinfo.io. Оба резолвера дали одно и то же, то есть это не
// «особенность европейской точки»: имена действительно указывают за границу.
//
// Блокировать их нельзя. Записи в hosts не «ускоряют YouTube» — они убирают имя
// из выбора. Убрать рабочий сервер значит оставить клиенту только ловушки,
// и видео станет хуже, а не лучше. Именно об этом предупреждал ekungurov
// в issue #18271, когда отделял российские имена от остальных.
const FOREIGN_WORKING = [
    "rr1---sn-1giz7n7l.googlevideo.com",   // 74.125.11.70 CH AS15169 Google LLC
    "rr1---sn-2of5f5-5i.c.youtube.com",   // 173.194.163.65 PL AS15169 Google LLC
    "rr1---sn-4g5e6ns6.googlevideo.com",   // 173.194.187.6 DE AS15169 Google LLC
    "rr1---sn-4g5e6nsr.googlevideo.com",   // 173.194.187.102 DE AS15169 Google LLC
    "rr1---sn-4g5e6nsz.googlevideo.com",   // 173.194.182.230 DE AS15169 Google LLC
    "rr1---sn-4g5e6nzs.googlevideo.com",   // 74.125.13.230 DE AS15169 Google LLC
    "rr1---sn-4g5edndl.googlevideo.com",   // 74.125.162.166 DE AS15169 Google LLC
    "rr1---sn-4g5ednsz.googlevideo.com",   // 173.194.188.134 DE AS15169 Google LLC
    "rr1---sn-4g5lznl6.googlevideo.com",   // 74.125.173.38 DE AS15169 Google LLC
    "rr1---sn-4g5lznl7.googlevideo.com",   // 74.125.163.166 DE AS15169 Google LLC
    "rr1---sn-4g5lznlz.googlevideo.com",   // 74.125.104.70 DE AS15169 Google LLC
    "rr1---sn-f5f7kn7e.c.youtube.com",   // 74.125.104.134 PL AS15169 Google LLC
    "rr1---sn-f5f7lnly.c.youtube.com",   // 74.125.153.230 PL AS15169 Google LLC
    "rr1---sn-oj1gi5-54.c.youtube.com",   // 173.194.164.145 CH AS15169 Google LLC
    "rr2---sn-1giz7n7l.googlevideo.com",   // 74.125.11.71 CH AS15169 Google LLC
    "rr2---sn-2of5f5-5i.c.youtube.com",   // 173.194.163.66 PL AS15169 Google LLC
    "rr2---sn-4g5e6ns6.googlevideo.com",   // 173.194.187.7 DE AS15169 Google LLC
    "rr2---sn-4g5e6nsr.googlevideo.com",   // 173.194.187.103 DE AS15169 Google LLC
    "rr2---sn-4g5e6nsz.googlevideo.com",   // 173.194.182.231 DE AS15169 Google LLC
    "rr2---sn-4g5e6nzs.googlevideo.com",   // 74.125.13.231 DE AS15169 Google LLC
    "rr2---sn-4g5edndl.googlevideo.com",   // 74.125.162.167 DE AS15169 Google LLC
    "rr2---sn-4g5ednsz.googlevideo.com",   // 173.194.188.135 DE AS15169 Google LLC
    "rr2---sn-4g5lznl6.googlevideo.com",   // 74.125.173.39 DE AS15169 Google LLC
    "rr2---sn-4g5lznl7.googlevideo.com",   // 74.125.163.167 DE AS15169 Google LLC
    "rr2---sn-4g5lznlz.googlevideo.com",   // 74.125.104.71 DE AS15169 Google LLC
    "rr2---sn-f5f7kn7e.c.youtube.com",   // 74.125.104.135 PL AS15169 Google LLC
    "rr2---sn-f5f7lnly.c.youtube.com",   // 74.125.153.231 PL AS15169 Google LLC
    "rr2---sn-oj1gi5-54.c.youtube.com",   // 173.194.164.146 CH AS15169 Google LLC
    "rr3---sn-1giz7n7l.googlevideo.com",   // 74.125.11.72 CH AS15169 Google LLC
    "rr3---sn-2of5f5-5i.c.youtube.com",   // 173.194.163.67 PL AS15169 Google LLC
    "rr3---sn-4g5e6ns6.googlevideo.com",   // 173.194.187.8 DE AS15169 Google LLC
    "rr3---sn-4g5e6nsr.googlevideo.com",   // 173.194.187.104 DE AS15169 Google LLC
    "rr3---sn-4g5e6nsz.googlevideo.com",   // 173.194.182.232 DE AS15169 Google LLC
    "rr3---sn-4g5e6nzs.googlevideo.com",   // 74.125.13.232 DE AS15169 Google LLC
    "rr3---sn-4g5edndl.googlevideo.com",   // 74.125.162.168 DE AS15169 Google LLC
    "rr3---sn-4g5ednsz.googlevideo.com",   // 173.194.188.136 DE AS15169 Google LLC
    "rr3---sn-4g5lznl6.googlevideo.com",   // 74.125.173.40 DE AS15169 Google LLC
    "rr3---sn-4g5lznl7.googlevideo.com",   // 74.125.163.168 DE AS15169 Google LLC
    "rr3---sn-4g5lznlz.googlevideo.com",   // 74.125.104.72 DE AS15169 Google LLC
    "rr3---sn-f5f7kn7e.c.youtube.com",   // 74.125.104.136 PL AS15169 Google LLC
    "rr3---sn-f5f7lnly.c.youtube.com",   // 74.125.153.232 PL AS15169 Google LLC
    "rr3---sn-oj1gi5-54.c.youtube.com",   // 173.194.164.147 CH AS15169 Google LLC
    "rr4---sn-1giz7n7l.googlevideo.com",   // 74.125.11.73 CH AS15169 Google LLC
    "rr4---sn-2of5f5-5i.c.youtube.com",   // 173.194.163.68 PL AS15169 Google LLC
    "rr4---sn-4g5e6ns6.googlevideo.com",   // 173.194.187.9 DE AS15169 Google LLC
    "rr4---sn-4g5e6nsr.googlevideo.com",   // 173.194.187.105 DE AS15169 Google LLC
    "rr4---sn-4g5e6nsz.googlevideo.com",   // 173.194.182.233 DE AS15169 Google LLC
    "rr4---sn-4g5e6nzs.googlevideo.com",   // 74.125.13.233 DE AS15169 Google LLC
    "rr4---sn-4g5edndl.googlevideo.com",   // 74.125.162.169 DE AS15169 Google LLC
    "rr4---sn-4g5ednsz.googlevideo.com",   // 173.194.188.137 DE AS15169 Google LLC
    "rr4---sn-4g5lznl6.googlevideo.com",   // 74.125.173.41 DE AS15169 Google LLC
    "rr4---sn-4g5lznl7.googlevideo.com",   // 74.125.163.169 DE AS15169 Google LLC
    "rr4---sn-4g5lznlz.googlevideo.com",   // 74.125.104.73 DE AS15169 Google LLC
    "rr4---sn-f5f7kn7e.c.youtube.com",   // 74.125.104.137 PL AS15169 Google LLC
    "rr4---sn-f5f7lnly.c.youtube.com",   // 74.125.153.233 PL AS15169 Google LLC
    "rr4---sn-oj1gi5-54.c.youtube.com",   // 173.194.164.148 CH AS15169 Google LLC
    "rr5---sn-1giz7n7l.googlevideo.com",   // 74.125.11.74 CH AS15169 Google LLC
    "rr5---sn-2of5f5-5i.c.youtube.com",   // 173.194.163.69 PL AS15169 Google LLC
    "rr5---sn-4g5e6ns6.googlevideo.com",   // 173.194.187.10 DE AS15169 Google LLC
    "rr5---sn-4g5e6nsr.googlevideo.com",   // 173.194.187.106 DE AS15169 Google LLC
    "rr5---sn-4g5e6nsz.googlevideo.com",   // 173.194.182.234 DE AS15169 Google LLC
    "rr5---sn-4g5e6nzs.googlevideo.com",   // 74.125.13.234 DE AS15169 Google LLC
    "rr5---sn-4g5edndl.googlevideo.com",   // 74.125.162.170 DE AS15169 Google LLC
    "rr5---sn-4g5ednsz.googlevideo.com",   // 173.194.188.138 DE AS15169 Google LLC
    "rr5---sn-4g5lznl6.googlevideo.com",   // 74.125.173.42 DE AS15169 Google LLC
    "rr5---sn-4g5lznl7.googlevideo.com",   // 74.125.163.170 DE AS15169 Google LLC
    "rr5---sn-4g5lznlz.googlevideo.com",   // 74.125.104.74 DE AS15169 Google LLC
    "rr5---sn-f5f7kn7e.c.youtube.com",   // 74.125.104.138 PL AS15169 Google LLC
    "rr5---sn-f5f7lnly.c.youtube.com",   // 74.125.153.234 PL AS15169 Google LLC
    "rr5---sn-oj1gi5-54.c.youtube.com",   // 173.194.164.149 CH AS15169 Google LLC
];

// Не заресолвились при проверке: догадка не основание блокировать.
const UNVERIFIED = [
  "rr5---sn-hgn7rnll.googlevideo.com",
  "rr4---sn-q4fl6nsd.googlevideo.com",
  "rr5---sn-q4fl6n6z.googlevideo.com"
];

const BLOCK = "0.0.0.0";

// --- собираем
const excluded = new Set(
  [...NOT_RUSSIAN, ...UNVERIFIED, ...FOREIGN_WORKING, ...PHANTOM].map((h) => h.toLowerCase())
);

// Фантомы не должны вернуться в TRAPS: если кто-то скопирует список из issue
// целиком, они снова окажутся в файле. Проверяем ДО сборки, чтобы ошибка была
// внятной, а не «в файле 174 вместо 147».
const phantomInTraps = TRAPS
  .map((h) => h.trim().toLowerCase())
  .filter((h) => PHANTOM.includes(h));

if (phantomInTraps.length > 0) {
  console.error("ОШИБКА: в TRAPS есть несуществующие имена: " + phantomInTraps.join(", "));
  process.exit(1);
}

const seen = new Set();
const final = [];

for (const raw of TRAPS) {
  const host = raw.trim().toLowerCase();
  if (!host) continue;
  if (excluded.has(host)) continue;

  // ВАЖНО: `seen.add()` возвращает сам Set, а не true/false.
  // `if (!seen.add(host)) continue;` не отсеивает НИЧЕГО — так в файл
  // попадали дубли, и проверка «дублей 0» падала. Сначала has(), потом add().
  if (seen.has(host)) continue;
  seen.add(host);

  final.push(host);
}

final.sort();

// --- проверяем, что не протащили исключённое
const leaked = final.filter((h) => excluded.has(h));

if (leaked.length > 0) {
  console.error("ОШИБКА: в списке остались исключённые: " + leaked.join(", "));
  process.exit(1);
}

// --- пишем
const lines = [];

lines.push("### YouTube - ловушки российских кэшей (замедление видео)");
lines.push("#");
lines.push("# РКН подменил кэширующие серверы YouTube, которые стоят в России:");
lines.push("# имена остались прежними, а отдают они медленно или не отдают вовсе.");
lines.push("# Браузер берёт сервер из списка, который вернул YouTube, попадает на");
lines.push("# ловушку - и видео висит, хотя страница и картинки открываются.");
lines.push("#");
lines.push("# Список привязывает " + final.length + " известных имён-ловушек к 0.0.0.0, то есть");
lines.push("# в никуда. Тогда YouTube выбирает следующий сервер из своего списка,");
lines.push("# и если с зарубежными соединение работает - видео идёт.");
lines.push("#");
lines.push("# ЭТО НЕ ЛЕЧИТ ВСЁ. Если с зарубежными серверами связи нет, поможет");
lines.push("# только VPN или WARP: блокировка ловушек не создаёт маршрут там,");
lines.push("# где его нет.");
lines.push("#");
lines.push("# Почему имён так много: ловушки у каждого провайдера свои, а списки");
lines.push("# собирали разные люди (Дом.ру, Мегафон, МТС). Здесь объединение:");
lines.push("# лишнее имя, которого у вашего провайдера нет, ни на что не влияет -");
lines.push("# оно и так не резолвится.");
lines.push("#");
lines.push("# Чего здесь НЕТ:");
lines.push("#");
lines.push("#   * " + FOREIGN_WORKING.length + " имён, которые ведут на РАБОЧИЕ зарубежные серверы");
lines.push("#     Google (Германия, Польша, Швейцария). Записи в hosts не ускоряют");
lines.push("#     YouTube - они убирают имя из выбора. Убрать хороший сервер значит");
lines.push("#     оставить клиенту только ловушки;");
lines.push("#   * ещё " + NOT_RUSSIAN.length + " имён из того же разряда (Брюссель, Торонто,");
lines.push("#     Маунтин-Вью) - их проверили отдельно, адреса в генераторе;");
lines.push("#   * " + UNVERIFIED.length + " имени, которые при проверке не заресолвились: догадка");
lines.push("#     «наверное тоже не в России» - не основание блокировать;");
lines.push("#   * " + PHANTOM.length + " имён, которых не существует. Авторы списков дописали");
lines.push("#     rr1..rr10 «на всякий случай», хотя у тех семейств есть только rr1..rr5.");
lines.push("#     Проверено резолвом каждого номера от rr1 до rr12.");
lines.push("#");
lines.push("# Если стало хуже - снимите галочку и сохраните hosts.");
lines.push("#");
lines.push("# Источник: github.com/Flowseal/zapret-discord-youtube/issues/18271");
lines.push("# Сгенерировано: tools/gen-youtube-traps.js");
lines.push("#");
lines.push("# Перезапустите браузер после применения: он держит DNS в своём кэше.");

for (const host of final) lines.push(BLOCK + " " + host);

const target = path.join(__dirname, "..", "assets", "hosts", "youtube-traps.txt");

fs.writeFileSync(target, lines.join("\n") + "\n", "utf8");

console.log("записано: " + target);
console.log("имён:     " + final.length);
console.log(
  "исключено: " + (NOT_RUSSIAN.length + UNVERIFIED.length + FOREIGN_WORKING.length + PHANTOM.length) +
    " (зарубежных " + NOT_RUSSIAN.length + ", непроверенных " + UNVERIFIED.length +
    ", рабочих зарубежных " + FOREIGN_WORKING.length + ", несуществующих " + PHANTOM.length + ")"
);
