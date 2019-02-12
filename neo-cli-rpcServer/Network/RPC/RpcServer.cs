using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Neo.IO.Json;
using Neo.Ledger;
using Neo.Persistence.LightDB;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Neo.IO;
using Neo.Network.P2P.Payloads;
using Neo;
using Neo.Network.RPC;
using Neo.IO.Data.LightDB;
using System.Collections.Generic;
using Neo.IO.Wrappers;

namespace neo_cli_rpcServer
{
    public sealed class RpcServer : IDisposable
    {
        private IWebHost host;

        private DB db;

        public RpcServer(DB _db)
        {
            db = _db;
        }

        private static JObject CreateErrorResponse(JObject id, int code, string message, JObject data = null)
        {
            JObject response = CreateResponse(id);
            response["error"] = new JObject();
            response["error"]["code"] = code;
            response["error"]["message"] = message;
            if (data != null)
                response["error"]["data"] = data;
            return response;
        }

        private static JObject CreateResponse(JObject id)
        {
            JObject response = new JObject();
            response["jsonrpc"] = "2.0";
            response["id"] = id;
            return response;
        }
        private JObject Process(string method, JArray _params)
        {
            switch (method)
            {
                case "getblock":
                    List<UInt256> header_index = new List<UInt256>();
                    Block block;
                    UInt256 hash;
                    var snapshot = db.UseSnapShot();
                    //header_index.AddRange(db.Find(snapshot, SliceBuilder.Begin(Prefixes.IX_HeaderHashList).Add(new byte[0]), (k, v) => new KeyValuePair<UInt32Wrapper, HeaderHashList>(k.ToArray().AsSerializable<UInt32Wrapper>(1), v.ToArray().AsSerializable<HeaderHashList>())).OrderBy(p => (uint)p.Key).SelectMany(p => p.Value.Hashes));
                    if (_params[0] is JNumber)
                    {
                        uint index = (uint)_params[0].AsNumber();
                        hash = header_index[(int)index];
                    }
                    else
                    {
                        hash = UInt256.Parse(_params[0].AsString());
                    }
                    var val = snapshot.GetValue(new byte[] { }, (new byte[] { Prefixes.DATA_Block }).Concat(hash.ToArray()).ToArray())?.value;
                    BlockState blockstate = val.AsSerializable<BlockState>();
                    Transaction[] trans = blockstate.TrimmedBlock.Hashes.Select(p =>
                        snapshot.GetValue(new byte[] { }, (new byte[] { Prefixes.DATA_Transaction }).Concat(p.ToArray()).ToArray()).value.AsSerializable<TransactionState>().Transaction
                    ).ToArray();
                    block = new Block
                    {
                        Version = blockstate.TrimmedBlock.Version,
                        PrevHash = blockstate.TrimmedBlock.PrevHash,
                        MerkleRoot = blockstate.TrimmedBlock.MerkleRoot,
                        Timestamp = blockstate.TrimmedBlock.Timestamp,
                        Index = blockstate.TrimmedBlock.Index,
                        ConsensusData = blockstate.TrimmedBlock.ConsensusData,
                        NextConsensus = blockstate.TrimmedBlock.NextConsensus,
                        Witness = blockstate.TrimmedBlock.Witness,
                        Transactions = trans
                    };
                    bool verbose = _params.Count >= 2 && _params[1].AsBooleanOrDefault(false);
                    if (verbose)
                    {
                        JObject json = block.ToJson();
                        json["confirmations"] = snapshot.DataHeight - block.Index + 1;
                        //hash = header_index[(int)block.Index+1];
                        //if (hash != null)
                        //    json["nextblockhash"] = hash.ToString();
                        return json;
                    }
                    return block.ToArray().ToHexString();
                default:
                    throw new RpcException(-32601, "Method not found");
            }
        }

        private async Task ProcessAsync(HttpContext context)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            context.Response.Headers["Access-Control-Max-Age"] = "31536000";
            if (context.Request.Method != "GET" && context.Request.Method != "POST") return;
            JObject request = null;
            if (context.Request.Method == "GET")
            {
                string jsonrpc = context.Request.Query["jsonrpc"];
                string id = context.Request.Query["id"];
                string method = context.Request.Query["method"];
                string _params = context.Request.Query["params"];
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(method) && !string.IsNullOrEmpty(_params))
                {
                    try
                    {
                        _params = Encoding.UTF8.GetString(Convert.FromBase64String(_params));
                    }
                    catch (FormatException) { }
                    request = new JObject();
                    if (!string.IsNullOrEmpty(jsonrpc))
                        request["jsonrpc"] = jsonrpc;
                    request["id"] = id;
                    request["method"] = method;
                    request["params"] = JObject.Parse(_params);
                }
            }
            else if (context.Request.Method == "POST")
            {
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    try
                    {
                        request = JObject.Parse(reader);
                    }
                    catch (FormatException) { }
                }
            }
            JObject response;
            if (request == null)
            {
                response = CreateErrorResponse(null, -32700, "Parse error");
            }
            else if (request is JArray array)
            {
                if (array.Count == 0)
                {
                    response = CreateErrorResponse(request["id"], -32600, "Invalid Request");
                }
                else
                {
                    response = array.Select(p => ProcessRequest(context, p)).Where(p => p != null).ToArray();
                }
            }
            else
            {
                response = ProcessRequest(context, request);
            }
            if (response == null || (response as JArray)?.Count == 0) return;
            context.Response.ContentType = "application/json-rpc";
            await context.Response.WriteAsync(response.ToString(), Encoding.UTF8);
        }

        private JObject ProcessRequest(HttpContext context, JObject request)
        {
            if (!request.ContainsProperty("id")) return null;
            if (!request.ContainsProperty("method") || !request.ContainsProperty("params") || !(request["params"] is JArray))
            {
                return CreateErrorResponse(request["id"], -32600, "Invalid Request");
            }
            JObject result = null;
            try
            {
                string method = request["method"].AsString();
                JArray _params = (JArray)request["params"];
                //不考虑插件
                //foreach (IRpcPlugin plugin in Plugin.RpcPlugins)
                //{
                //    result = plugin.OnProcess(context, method, _params);
                //    if (result != null) break;
                //}
                //if (result == null)
                    result = Process(method, _params);
            }
            catch (Exception ex)
            {
#if DEBUG
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message, ex.StackTrace);
#else
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message);
#endif
            }
            JObject response = CreateResponse(request["id"]);
            response["result"] = result;
            return response;
        }

        public void Start(IPAddress bindAddress, int port, string sslCert = null, string password = null, string[] trustedAuthorities = null)
        {
            host = new WebHostBuilder().UseKestrel(options => options.Listen(bindAddress, port, listenOptions =>
            {
                if (string.IsNullOrEmpty(sslCert)) return;
                listenOptions.UseHttps(sslCert, password, httpsConnectionAdapterOptions =>
                {
                    if (trustedAuthorities is null || trustedAuthorities.Length == 0)
                        return;
                    httpsConnectionAdapterOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    httpsConnectionAdapterOptions.ClientCertificateValidation = (cert, chain, err) =>
                    {
                        if (err != SslPolicyErrors.None)
                            return false;
                        X509Certificate2 authority = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                        return trustedAuthorities.Contains(authority.Thumbprint);
                    };
                });
            }))
            .Configure(app =>
            {
                app.UseResponseCompression();
                app.Run(ProcessAsync);
            })
            .ConfigureServices(services =>
            {
                services.AddResponseCompression(options =>
                {
                    // options.EnableForHttps = false;
                    options.Providers.Add<GzipCompressionProvider>();
                    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json-rpc" });
                });

                services.Configure<GzipCompressionProviderOptions>(options =>
                {
                    options.Level = CompressionLevel.Fastest;
                });
            })
            .Build();

            host.Start();
        }

        public void Dispose()
        {
            if (host != null)
            {
                host.Dispose();
                host = null;
            }
        }

    }
}
