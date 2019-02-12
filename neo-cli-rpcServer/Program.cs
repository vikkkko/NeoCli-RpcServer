using Neo.IO.Data.LightDB;
using System;
using System.Net;


namespace neo_cli_rpcServer
{
    class Program
    {
        static void Main(string[] args)
        {
            DB db = DB.Open("D:\\_______work\\neo-cli-nel\\neo-cli-nel\\bin\\Debug\\netcoreapp2.0\\Chain_{0}", new Options { CreateIfMissing = true });
            RpcServer RpcServer = new RpcServer(db);
            RpcServer.Start(IPAddress.Parse("0.0.0.0"), 20338);
            Console.WriteLine("按任意键结束");
            Console.ReadLine();
        }
    }
}
