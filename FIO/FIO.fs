namespace FSharp.FIO

open System.Collections.Generic
open System
open System.Net
open System.Net.Sockets

module Channel =

    type Channel =
        | FIOQueue of Queue<string>
        | FIOSocket of Socket

    let getFIOSocket =
        let ipAddress = Dns.GetHostEntry("127.0.0.1").AddressList.[0]
        let port = 8888
        let endpoint = IPEndPoint(ipAddress, port)
        let s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        s.Bind(endpoint)
        s.Connect(endpoint)
        FIOSocket s

    let getFIOQueue = FIOQueue (Queue<string>())

    let stopChannel c =
        match c with
        | FIOSocket s -> s.Shutdown(SocketShutdown.Both)
                         s.Close()
        | _           -> ()

    let internal sendToSocket (s : Socket) (msg : string) = 
        let data = System.Text.Encoding.ASCII.GetBytes msg
        s.SendBufferSize <- Array.length data
        s.Send(data) |> ignore

    let internal receiveFromSocket (s : Socket) =
        let bytes = Array.create 1 (byte (0))
        s.ReceiveBufferSize <- Array.length bytes
        let len = s.Receive(bytes)
        let data = System.Text.Encoding.ASCII.GetString(bytes, 0, len)
        data
    
    let internal sendToChannel c v =
        match c with
        | FIOQueue q  -> q.Enqueue(v)
        | FIOSocket s -> sendToSocket s v
      
    let internal receiveFromChannel c =
        match c with
        | FIOQueue q  -> q.Dequeue()
        | FIOSocket s -> receiveFromSocket s

module FIO =

    type Effect<'a> =
        | Input of ('a -> Effect<'a>)
        | Output of 'a * (unit -> Effect<'a>)
        | Parallel of Effect<'a> * Effect<'a>
        | Unit

    let send (v, f) = Output(v, f)
    let receive f = Input f
    
    let rec naiveEval e c = 
        match e with 
        | Input f          -> try 
                                  let v = Channel.receiveFromChannel c
                                  printfn "Received message: '%s'" v
                                  naiveEval (f v) c
                              with 
                              | :? InvalidOperationException -> ()
        | Output(v, f)     -> Channel.sendToChannel c v
                              printfn "Sent message: '%s'" v
                              naiveEval (f ()) c
        | Parallel(e1, e2) -> async {
                                naiveEval e1 c
                              } |> Async.Start
                              naiveEval e2 c
        | Unit             -> ()