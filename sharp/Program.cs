﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoMod.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Olympus {
    public static class Program {

        public static string RootDirectory;
        public static string SelfPath;

        public static void Main(string[] args) {
            bool debug = false;
            bool verbose = false;

            for (int i = 1; i < args.Length; i++) {
                string arg = args[i];
                if (arg == "--debug") {
                    debug = true;
                } else if (arg == "--verbose") {
                    verbose = true;
                } else if (arg == "--console") {
                    AllocConsole();
                }
            }

            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            SelfPath = Assembly.GetExecutingAssembly().Location;
            RootDirectory = Path.GetDirectoryName(Environment.CurrentDirectory);
            Console.Error.WriteLine(RootDirectory);

            if (Type.GetType("Mono.Runtime") != null) {
                // Mono hates HTTPS.
                ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => {
                    return true;
                };
            }

            // Enable TLS 1.2 to fix connecting to GitHub.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            if (args.Length >= 1 && args[0] == "--uninstall" && PlatformHelper.Is(Platform.Windows)) {
                new CmdWin32AppUninstall().Run(args.Length >= 2 && args[1] == "--quiet");
                return;
            }

            Process parentProc = null;
            int parentProcID = 0;

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            List<Thread> threads = new List<Thread>();
            listener.Start();

            Console.WriteLine($"{((IPEndPoint) listener.LocalEndpoint).Port}");

            try {
                parentProc = Process.GetProcessById(parentProcID = int.Parse(args.Last()));
            } catch {
                Console.Error.WriteLine("[sharp] Invalid parent process ID");
            }

            if (debug) {
                Debugger.Launch();
                Console.WriteLine(@"""debug""");

            } else {
                Console.WriteLine(@"""ok""");
            }

            Console.WriteLine(@"null");
            Console.Out.Flush();

            if (parentProc != null) {
                Thread killswitch = new Thread(() => {
                    try {
                        while (!parentProc.HasExited && parentProc.Id == parentProcID) {
                            Thread.Yield();
                            Thread.Sleep(1000);
                        }
                        Environment.Exit(0);
                    } catch {
                        Environment.Exit(-1);
                    }
                }) {
                    Name = "Killswitch",
                    IsBackground = true
                };
                killswitch.Start();
            }

            Cmds.Init();

            try {
                while ((parentProc != null && !parentProc.HasExited && parentProc.Id == parentProcID) || parentProc == null) {
                    TcpClient client = listener.AcceptTcpClient();
                    try {
                        string ep = client.Client.RemoteEndPoint.ToString();
                        Console.Error.WriteLine($"[sharp] New TCP connection: {ep}");

                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 1000);
                        Stream stream = client.GetStream();

                        MessageContext ctx = new MessageContext();

                        Thread threadW = new Thread(() => {
                            try {
                                using (StreamWriter writer = new StreamWriter(stream))
                                    WriteLoop(parentProc, ctx, writer, verbose);
                            } catch (Exception e) {
                                if (e is ObjectDisposedException)
                                    Console.Error.WriteLine($"[sharp] Failed writing to {ep}: {e.GetType()}: {e.Message}");
                                else
                                    Console.Error.WriteLine($"[sharp] Failed writing to {ep}: {e}");
                                client.Close();
                            } finally {
                                ctx.Dispose();
                            }
                        }) {
                            Name = $"Write Thread for Connection {ep}",
                            IsBackground = true
                        };

                        Thread threadR = new Thread(() => {
                            try {
                                using (StreamReader reader = new StreamReader(stream))
                                    ReadLoop(parentProc, ctx, reader, verbose);
                            } catch (Exception e) {
                                if (e is ObjectDisposedException)
                                    Console.Error.WriteLine($"[sharp] Failed reading from {ep}: {e.GetType()}: {e.Message}");
                                else
                                    Console.Error.WriteLine($"[sharp] Failed reading from {ep}: {e}");
                                client.Close();
                            } finally {
                                ctx.Dispose();
                            }
                        }) {
                            Name = $"Read Thread for Connection {ep}",
                            IsBackground = true
                        };

                        threads.Add(threadW);
                        threads.Add(threadR);
                        threadW.Start();
                        threadR.Start();

                    } catch (ThreadAbortException) {

                    } catch (Exception e) {
                        Console.Error.WriteLine($"[sharp] Failed listening for TCP connection:\n{e}");
                        client.Close();
                    }
                }

            } catch (ThreadAbortException) {

            } catch (Exception e) {
                Console.Error.WriteLine($"[sharp] Failed listening for TCP connection:\n{e}");
            }

            Console.Error.WriteLine("[sharp] Goodbye");

        }

        public static void WriteLoop(Process parentProc, MessageContext ctx, TextWriter writer, bool verbose) {
            JsonSerializer jsonSerializer = new JsonSerializer();

            using (JsonTextWriter jsonWriter = new JsonTextWriter(writer)) {
                for (Message msg = null; !(parentProc?.HasExited ?? false) && (msg = ctx.WaitForNext()) != null;) {
                    object value = msg.Value;
                    
                    if (value is IEnumerator enumerator) {
                        Console.Error.WriteLine($"[sharp] New CmdTask: {msg.UID}");
                        CmdTasks.Add(new CmdTask(msg.UID, enumerator));
                        value = msg.UID;
                    }

                    msg.Value = value;

                    jsonSerializer.Serialize(jsonWriter, msg);
                    jsonWriter.Flush();
                    writer.WriteLine();
                    writer.Flush();
                }
            }
        }


        public static void ReadLoop(Process parentProc, MessageContext ctx, TextReader reader, bool verbose, char delimiter = '\0') {
            // JsonTextReader would be neat here but Newtonsoft.Json is unaware of NetworkStreams and tries to READ PAST STRINGS
            while (!(parentProc?.HasExited ?? false)) {
                // Commands from Olympus come in pairs of two objects:

                if (verbose)
                    Console.Error.WriteLine("[sharp] Awaiting next command");

                Message msg = new Message();

                // Unique ID
                msg.UID = JsonConvert.DeserializeObject<string>(reader.ReadTerminatedString(delimiter));
                if (verbose)
                    Console.Error.WriteLine($"[sharp] Receiving command {msg.UID}");

                // Command ID
                string cid = JsonConvert.DeserializeObject<string>(reader.ReadTerminatedString(delimiter)).ToLowerInvariant();
                Cmd cmd = Cmds.Get(cid);
                if (cmd == null) {
                    reader.ReadTerminatedString(delimiter);
                    Console.Error.WriteLine($"[sharp] Unknown command {cid}");
                    msg.Error = "cmd failed running: not found: " + cid;
                    ctx.Reply(msg);
                    continue;
                }

                if (verbose)
                    Console.Error.WriteLine($"[sharp] Parsing args for {cid}");

                // Payload
                object input = JsonConvert.DeserializeObject(reader.ReadTerminatedString(delimiter), cmd.InputType);
                object output;
                try {
                    if (verbose || cmd.LogRun)
                        Console.Error.WriteLine($"[sharp] Executing {cid}");
                    if (cmd.Taskable) {
                        output = Task.Run(() => cmd.Run(input));
                    } else {
                        output = cmd.Run(input);
                    }

                } catch (Exception e) {
                    Console.Error.WriteLine($"[sharp] Failed running {cid}: {e}");
                    msg.Error = "cmd failed running: " + e;
                    ctx.Reply(msg);
                    continue;
                }

                if (output is Task<object> task) {
                    task.ContinueWith(t => {
                        if (task.Exception != null) {
                            Exception e = task.Exception;
                            Console.Error.WriteLine($"[sharp] Failed running task {cid}: {e}");
                            msg.Error = "cmd task failed running: " + e;
                            ctx.Reply(msg);
                            return;
                        }

                        msg.Value = t.Result;
                        ctx.Reply(msg);
                    });

                } else {
                    msg.Value = output;
                    ctx.Reply(msg);
                }
            }
        }

        [DllImport("kernel32")]
        public static extern bool AllocConsole();

        public static string ReadTerminatedString(this TextReader reader, char delimiter) {
            StringBuilder sb = new StringBuilder();
            char c;
            while ((c = (char) reader.Read()) != delimiter) {
                if (c < 0) {
                    // TODO: handle network stream end?
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }


        public class MessageContext : IDisposable {

            public readonly ManualResetEvent Event = new ManualResetEvent(false);
            public readonly WaitHandle[] EventWaitHandles;

            public readonly ConcurrentQueue<Message> Queue = new ConcurrentQueue<Message>();

            public bool Disposed;

            public MessageContext() {
                EventWaitHandles = new WaitHandle[] { Event };
            }

            public void Reply(Message msg) {
                Queue.Enqueue(msg);

                try {
                    Event.Set();
                } catch {
                }
            }

            public Message WaitForNext() {
                if (Queue.TryDequeue(out Message msg))
                    return msg;

                while (!Disposed) {
                    try {
                        WaitHandle.WaitAny(EventWaitHandles, 2000);
                    } catch {
                    }

                    if (Queue.TryDequeue(out msg)) {
                        if (Queue.Count == 0) {
                            try {
                                Event.Reset();
                            } catch {
                            }
                        }

                        return msg;
                    }
                }

                return null;
            }

            public void Dispose() {
                if (Disposed)
                    return;
                Disposed = true;

                Event.Set();
                Event.Dispose();
            }
        }

        public class Message {
            public string UID;
            public object Value;
            public string Error;
        }

    }
}
