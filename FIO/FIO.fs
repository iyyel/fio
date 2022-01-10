namespace FSharp.FIO

open System.Collections.Generic

module FIO =
    
    let defaultChannel = Queue<'a>()

    type Effect<'a> =
        | Input of ('a -> Effect<'a>)
        | Output of 'a * (unit -> Effect<'a>)
        | Parallel of Effect<'a> * Effect<'a>
        | Unit

    let send (v, f) = Output(v, f)
    let receive f = Input f
    
    let sendToChannel (c : Queue<'a>) v =
        c.Enqueue v
    
    let receiveFromChannel (c : Queue<'a>) =
        if c.Count = 0 then
            "error: channel is empty!"
        else
            c.Dequeue()
        
    let rec naiveEval e = 
        match e with 
        | Input f          -> let v = receiveFromChannel defaultChannel
                              printfn "Received message: '%s'" v
                              naiveEval(f v)
        | Output(v, f)     -> sendToChannel defaultChannel v
                              printfn "Sent message: '%s'" v
                              naiveEval(f ())
        | Parallel(e1, e2) -> async {
                                naiveEval(e1)
                              } |> Async.Start
                              naiveEval(e2)
        | Unit             -> ()