using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    /// <summary>Один список движка: что это, откуда и как его править.</summary>
    public sealed class ListKind
    {
        public string Id = "";

        /// <summary>Имя файла в engine\lists.</summary>
        public string File = "";

        public string Title = "";
        public string Note = "";

        /// <summary>true — личный список пользователя, false — скачиваемый.</summary>
        public bool User;

        /// <summary>true — адреса и подсети, false — домены.</summary>
        public bool Cidr;
    }

    /// <summary>
    /// Каталог всех списков движка.
    ///
    /// Зачем: раньше в интерфейсе было ровно два списка (мои домены и мои
    /// исключения), а остальные — list-general, list-google, ipset-all — можно
    /// было только перезаписать кнопкой «Обновить». Посмотреть, что внутри, и
    /// поправить одну строку было нельзя, хотя именно там и живут настоящие
    /// правила обхода.
    ///
    /// Все списки лежат в engine\lists и различаются только форматом значений:
    /// домены (с `!` для исключений) или адреса/CIDR. Поэтому один редактор
    /// с переключателем, а не пять экранов.
    /// </summary>
    public static class ListCatalog
    {
        public static List<ListKind> All()
        {
            return new List<ListKind>
            {
                new ListKind
                {
                    Id = "general",
                    File = HostlistManager.FileGeneral,
                    Title = "Мои домены",
                    Note = "Личные домены для обхода. Строка с «!» — исключение",
                    User = true
                },
                new ListKind
                {
                    Id = "exclude",
                    File = HostlistManager.FileExclude,
                    Title = "Мои исключения",
                    Note = "Сайты, которые обходить не нужно (банки, госуслуги)",
                    User = true
                },
                new ListKind
                {
                    Id = "general-default",
                    File = "list-general.txt",
                    Title = "Основной список доменов",
                    Note = "Скачивается вместе с движком: discord, youtube, cloudflare и т.д. Правки перезапишутся при обновлении списков",
                    User = false
                },
                new ListKind
                {
                    Id = "google",
                    File = "list-google.txt",
                    Title = "Список Google",
                    Note = "Отдельный профиль для youtube: googlevideo, ytimg, gstatic. Правки перезапишутся при обновлении",
                    User = false
                },
                new ListKind
                {
                    Id = "exclude-default",
                    File = "list-exclude.txt",
                    Title = "Список исключений",
                    Note = "Большой список сайтов, которые обход не трогает. Правки перезапишутся при обновлении",
                    User = false
                },
                new ListKind
                {
                    Id = "ipset",
                    File = "ipset-all.txt",
                    Title = "IP-подсети для обхода",
                    Note = "Десятки тысяч CIDR: обход по адресу, а не по имени. Скачивается кнопкой «Обновить»",
                    User = false,
                    Cidr = true
                },
                new ListKind
                {
                    Id = "ipset-exclude",
                    File = "ipset-exclude.txt",
                    Title = "Локальные сети",
                    Note = "Адреса, которые обход не должен трогать: 10/8, 192.168/16 и т.д.",
                    User = false,
                    Cidr = true
                },
                new ListKind
                {
                    Id = "ipset-exclude-user",
                    File = HostlistManager.FileIpsetExclude,
                    Title = "Мои локальные сети",
                    Note = "Личные исключения по адресам и подсетям",
                    User = true,
                    Cidr = true
                }
            };
        }

        public static ListKind Find(string id)
        {
            foreach (ListKind kind in All())
                if (string.Equals(kind.Id, id, StringComparison.OrdinalIgnoreCase)) return kind;

            return null;
        }

        public static string PathOf(ListKind kind)
        {
            if (kind == null) return "";
            return System.IO.Path.Combine(Paths.EngineListsDir, kind.File);
        }

        /// <summary>Размер файла в строках и байтах — для подписи в интерфейсе.</summary>
        public static void SizeOf(ListKind kind, out int lines, out long bytes)
        {
            lines = 0;
            bytes = 0;

            try
            {
                string path = PathOf(kind);
                if (!System.IO.File.Exists(path)) return;

                bytes = new FileInfo(path).Length;

                foreach (string line in File.ReadLines(path))
                    if (line.Trim().Length > 0) lines++;
            }
            catch { }
        }
    }
}