using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Проверка прокси без Telegram-клиента.
    ///
    /// Что проверяем по шагам (снизу вверх, чтобы ошибка была локализована):
    ///   1. прямой TCP+TLS до kws*.web.telegram.org  — сеть вообще пускает?
    ///   2. WS-апгрейд на /apiws                      — эндпоинт живой?
    ///   3. наш прокси слушает и отвечает handshake   — наша часть работает?
    ///   4. полный путь через прокси                  — всё вместе?
    /// </summary>
    public static class ProxySelfTest
    {
        public sealed class Step
        {
            public string Name = "";
            public bool Ok;
            public string Detail = "";
        }

        public static List<Step> Run(int dc, bool verbose)
        {
            List<Step> steps = new List<Step>();

            steps.Add(StepDirectTls(dc, verbose));
            steps.Add(StepWsUpgrade(dc, verbose));
            steps.Add(StepLocalHandshake(dc, verbose));

            return steps;
        }

        // ------------------------------------------------------------ 1: TLS

        private static Step StepDirectTls(int dc, bool verbose)
        {
            Step step = new Step { Name = "TCP+TLS до Telegram" };

            // Тот же порядок, что у реального подключения: иначе проверка
            // говорит «ок» по первому домену, а прокси в работе ходит через
            // другой — и «проверка прошла, а не работает» снова.
            List<string> domains = UpstreamRoutes.Order(dc, false, DcTable.WsDomains(dc, false));

            string error = "";

            foreach (string domain in domains)
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        IAsyncResult ar = client.BeginConnect(domain, 443, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(8000))
                        {
                            error = domain + ": таймаут TCP";
                            UpstreamRoutes.Fail(domain, true);
                            continue;
                        }
                        client.EndConnect(ar);

                        using (SslStream ssl = new SslStream(client.GetStream(), false, delegate { return true; }))
                        {
                            ssl.AuthenticateAsClient(domain);

                            step.Ok = true;
                            step.Detail = domain + " — " + ssl.SslProtocol;

                            UpstreamRoutes.Remember(dc, false, domain);
                        }
                    }

                    break;
                }
                catch (Exception ex)
                {
                    error = domain + ": " + Short(ex);
                    UpstreamRoutes.Fail(domain, false);
                }
            }

            if (!step.Ok) step.Detail = error;

            if (verbose) Log.Info("PROXY", "шаг 1: " + (step.Ok ? "ok" : "FAIL") + " " + step.Detail);
            return step;
        }

        // ------------------------------------------------------------- 2: WS

        private static Step StepWsUpgrade(int dc, bool verbose)
        {
            Step step = new Step { Name = "WebSocket /apiws" };
            List<string> domains = DcTable.WsDomains(dc, false);
            string domain = domains[0];

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(domain, 443, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(8000))
                    {
                        step.Detail = "таймаут TCP";
                        return step;
                    }
                    client.EndConnect(ar);

                    using (SslStream ssl = new SslStream(client.GetStream(), false, delegate { return true; }))
                    {
                        ssl.AuthenticateAsClient(domain);

                        string error;
                        WsClient ws = WsClient.Connect(ssl, domain, DcTable.WsPath, 8000, out error);

                        if (ws == null)
                        {
                            step.Detail = error;
                            return step;
                        }

                        step.Ok = true;
                        step.Detail = domain + DcTable.WsPath + " — 101";
                        ws.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                step.Detail = domain + ": " + Short(ex);
            }

            if (verbose) Log.Info("PROXY", "шаг 2: " + (step.Ok ? "ok" : "FAIL") + " " + step.Detail);
            return step;
        }

        // ------------------------------------------------- 3: наш прокси

        /// <summary>
        /// Поднимает прокси на свободном порту, подключается к нему как клиент
        /// и проверяет, что handshake разобран и в ответ пришли 64 байта.
        /// </summary>
        private static Step StepLocalHandshake(int dc, bool verbose)
        {
            Step step = new Step { Name = "наш прокси: handshake" };

            int port = FindFreePort();
            string secretHex = Settings.NewSecret();
            byte[] secret = TgProxyServer.ParseSecret(secretHex);

            TgProxyServer server = new TgProxyServer();
            try
            {
                string error = server.Start("127.0.0.1", port, secretHex);
                if (error != null)
                {
                    step.Detail = "не запустился: " + error;
                    return step;
                }

                using (TcpClient client = new TcpClient())
                {
                    client.Connect("127.0.0.1", port);
                    client.ReceiveTimeout = 15000;

                    NetworkStream stream = client.GetStream();

                    // строим init как клиент Telegram
                    byte[] init = BuildClientInit(secret, dc);

                    stream.Write(init, 0, init.Length);
                    stream.Flush();

                    byte[] response = new byte[64];
                    int read = 0;
                    DateTime until = DateTime.UtcNow.AddSeconds(15);

                    while (read < 64 && DateTime.UtcNow < until)
                    {
                        int n = stream.Read(response, read, 64 - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    if (read == 0)
                    {
                        step.Detail = "прокси не ответил (не смог подключиться к Telegram?)";
                        return step;
                    }

                    if (read < 64)
                    {
                        step.Detail = "получено " + read + " из 64 байт";
                        return step;
                    }

                    step.Ok = true;
                    step.Detail = "handshake принят, ответ 64 байта";
                }
            }
            catch (Exception ex)
            {
                step.Detail = Short(ex);
            }
            finally
            {
                try { server.Stop(); } catch { }
                try { server.Dispose(); } catch { }
            }

            if (verbose) Log.Info("PROXY", "шаг 3: " + (step.Ok ? "ok" : "FAIL") + " " + step.Detail);
            return step;
        }

        // ----------------------------------------------------------- helpers

        public static byte[] BuildClientInit(byte[] secret, int dc)
        {
            byte[] init = new byte[64];
            using (System.Security.Cryptography.RandomNumberGenerator rng =
                   System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(init);
            }

            byte[] prekey = new byte[32];
            Buffer.BlockCopy(init, 8, prekey, 0, 32);
            byte[] key = MtProtoHandshake.DeriveKey(prekey, secret);

            byte[] iv = new byte[16];
            Buffer.BlockCopy(init, 40, iv, 0, 16);

            byte[] tail = new byte[8];
            Buffer.BlockCopy(MtProtoHandshake.TagIntermediate, 0, tail, 0, 4);
            tail[4] = (byte)(dc & 0xFF);
            tail[5] = (byte)((dc >> 8) & 0xFF);

            using (AesCtr ctr = new AesCtr(key, iv))
            {
                ctr.Skip(56);
                ctr.Transform(tail, 0, 8);
            }

            Buffer.BlockCopy(tail, 0, init, 56, 8);
            return init;
        }

        public static int FindFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private static string Short(Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            string message = inner.Message;
            if (message.Length > 90) message = message.Substring(0, 87) + "…";
            return inner.GetType().Name + ": " + message;
        }
    }
}
