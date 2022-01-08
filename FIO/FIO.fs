namespace FSharp.FIO

open System.Net.Sockets
open System.Net
open System

module FIO =

    type Effect<'a> =
        | Input of ('a -> Effect<'a>)
        | Output of 'a
        | Parallel of Effect<'a> * Effect<'a>

    let send v = Output v
    let receive (f: 'a -> Effect<'a>) = Input(f)

    let rec naiveEval e = 
        match e with 
        | Input(f)         -> failwith "not implemented!"
        | Output(v)        -> failwith "not implemented!"
        | Parallel(e1, e2) -> failwith "not implemented!"
    
    let socketSend addr port (msg : string) =
        try
            let tcpClient = new TcpClient(addr, port)
            let data = System.Text.Encoding.ASCII.GetBytes msg
            let networkStream = tcpClient.GetStream()
            networkStream.Write(data, 0, Array.length data)
            printfn "Sent message: %s" msg
            networkStream.Close()
            networkStream.Dispose()
        with
        | :? ArgumentNullException as excp ->
            printfn "ArgumentNullException encountered: %s" excp.Message
        | :? SocketException as excp ->
            printfn "SocketException encountered: %s" excp.Message

    let socketReceive =
        let addr = IPAddress.Parse("127.0.0.1")
        let port = 8888
        let tcpListener = new TcpListener (addr, port)
        try
            tcpListener.Start()
            let bytes = Array.create 256 (byte (0))
            printfn "Waiting for a connection..."
            let tcpClient = tcpListener.AcceptTcpClient()
            let ns = tcpClient.GetStream()
            let mutable i = ns.Read(bytes, 0, (Array.length bytes))
            while i <> 0 do
                let data = System.Text.Encoding.ASCII.GetString(bytes, 0, i)
                printfn "Received: %s" data
            tcpClient.Close()
        with
        | excp -> printfn "%s" excp.Message
        tcpListener.Stop()