using System;
using System.Collections.Generic;
using System.Net;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Крошечный DNS-ответчик: принимает запрос, спрашивает DoH, собирает ответ.
    ///
    /// Зачем свой, если есть DoH в прокси. Прокси резолвит только те имена,
    /// которые приходят к нему в SOCKS5-запросе. Но в VPN-режиме приложения
    /// резолвят имена САМИ — и получают от оператора подлог (youtube ->
    /// 198.18.0.4). Приложение идёт по этому адресу, попадает в чёрную дыру и
    /// показывает «нет соединения», хотя туннель работает.
    ///
    /// Поэтому мы перехватываем UDP на порт 53 прямо в ретрансляторе и
    /// отвечаем сами. Тогда честные адреса получают ВСЕ приложения, включая
    /// те, что ходят на зашитый 8.8.8.8.
    /// </summary>
    public static class DnsServer
    {
        /// <summary>Похоже ли это на DNS-запрос (порт 53 и разумная длина).</summary>
        public static bool IsQuery(byte[] data)
        {
            if (data == null || data.Length < 12) return false;

            // QR=0 (запрос), OPCODE=0 (обычный запрос), QDCOUNT>0
            if ((data[2] & 0x80) != 0) return false;

            int questions = (data[4] << 8) | data[5];

            return questions > 0;
        }

        /// <summary>
        /// Ответить на запрос. null — не смогли (тогда вызывающий решает, что
        /// делать: обычно просто не отвечать, клиент повторит).
        /// </summary>
        public static byte[] Answer(byte[] query)
        {
            try
            {
                int id = (query[0] << 8) | query[1];

                string host;
                int qtype;
                int questionEnd;

                if (!ParseQuestion(query, out host, out qtype, out questionEnd)) return null;

                List<IPAddress> addresses = Doh.Resolve(host);

                if (addresses.Count == 0) return BuildEmpty(id, query, questionEnd, 3);  // NXDOMAIN

                // Оставляем только те записи, о которых спросили.
                // 1 = A, 28 = AAAA, 255 = ANY (отдаём всё).
                List<IPAddress> picked = new List<IPAddress>();

                foreach (IPAddress ip in addresses)
                {
                    bool v4 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

                    if (qtype == 1 && v4) picked.Add(ip);
                    else if (qtype == 28 && !v4) picked.Add(ip);
                    else if (qtype == 255) picked.Add(ip);
                }

                // Спросили AAAA, а IPv6 нет — это НЕ ошибка: пустой ответ
                // говорит клиенту «IPv6 здесь нет», и он идёт по IPv4.
                return BuildResponse(id, query, questionEnd, picked);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Разобрать вопрос: имя, тип, где он кончается.</summary>
        private static bool ParseQuestion(byte[] query, out string host, out int qtype, out int end)
        {
            host = "";
            qtype = 0;
            end = 0;

            int p = 12;
            System.Text.StringBuilder name = new System.Text.StringBuilder();

            while (p < query.Length)
            {
                int length = query[p];

                if (length == 0) { p++; break; }
                if (length > 63) return false;          // сжатие в вопросе не бывает

                if (p + 1 + length > query.Length) return false;

                if (name.Length > 0) name.Append('.');

                name.Append(System.Text.Encoding.ASCII.GetString(query, p + 1, length));

                p += 1 + length;
            }

            if (p + 4 > query.Length) return false;

            qtype = (query[p] << 8) | query[p + 1];
            end = p + 4;                                 // тип + класс

            host = name.ToString();

            return host.Length > 0;
        }

        private static byte[] BuildResponse(int id, byte[] query, int questionEnd, List<IPAddress> addresses)
        {
            List<byte[]> records = new List<byte[]>();

            foreach (IPAddress ip in addresses)
            {
                byte[] raw = ip.GetAddressBytes();

                // NAME = указатель на вопрос (0xC00C), TYPE, CLASS=IN, TTL=60, RDLENGTH, RDATA
                byte[] record = new byte[12 + raw.Length];

                record[0] = 0xC0;
                record[1] = 0x0C;

                int type = raw.Length == 4 ? 1 : 28;

                record[2] = (byte)(type >> 8);
                record[3] = (byte)(type & 0xFF);

                record[4] = 0;      // CLASS hi
                record[5] = 1;      // CLASS lo = IN

                record[6] = 0;      // TTL 60
                record[7] = 0;
                record[8] = 0;
                record[9] = 60;

                record[10] = (byte)(raw.Length >> 8);
                record[11] = (byte)(raw.Length & 0xFF);

                Buffer.BlockCopy(raw, 0, record, 12, raw.Length);

                records.Add(record);
            }

            int size = questionEnd;

            foreach (byte[] record in records) size += record.Length;

            byte[] response = new byte[size];

            // Заголовок: id, флаги (QR=1, RD=1, RA=1), QDCOUNT=1, ANCOUNT=N
            response[0] = (byte)(id >> 8);
            response[1] = (byte)(id & 0xFF);
            response[2] = 0x81;      // QR + RD
            response[3] = 0x80;      // RA
            response[4] = 0;
            response[5] = 1;
            response[6] = (byte)(records.Count >> 8);
            response[7] = (byte)(records.Count & 0xFF);
            response[8] = 0;         // NSCOUNT
            response[9] = 0;
            response[10] = 0;        // ARCOUNT
            response[11] = 0;

            // Сам вопрос копируем как есть — клиент сверяет его с отправленным.
            Buffer.BlockCopy(query, 12, response, 12, questionEnd - 12);

            int offset = questionEnd;

            foreach (byte[] record in records)
            {
                Buffer.BlockCopy(record, 0, response, offset, record.Length);
                offset += record.Length;
            }

            return response;
        }

        private static byte[] BuildEmpty(int id, byte[] query, int questionEnd, int rcode)
        {
            byte[] response = new byte[questionEnd];

            response[0] = (byte)(id >> 8);
            response[1] = (byte)(id & 0xFF);
            response[2] = (byte)(0x81 | (rcode & 0x0F));
            response[3] = 0x80;
            response[4] = 0;
            response[5] = 1;
            response[6] = 0;
            response[7] = 0;
            response[8] = 0;
            response[9] = 0;
            response[10] = 0;
            response[11] = 0;

            Buffer.BlockCopy(query, 12, response, 12, questionEnd - 12);

            return response;
        }
    }
}
