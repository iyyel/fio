namespace FSharp.FIO

open System.Collections.Generic
open System

module internal Channel =

    let queue = Queue<'a>()
    
    let sendToChannel (c : Queue<'a>) v =
          c.Enqueue v
      
    let receiveFromChannel (c : Queue<'a>) =
          c.Dequeue()

module FIO =

    type Effect<'a> =
        | Input of ('a -> Effect<'a>)
        | Output of 'a * (unit -> Effect<'a>)
        | Parallel of Effect<'a> * Effect<'a>
        | Unit

    let send (v, f) = Output(v, f)
    let receive f = Input f
    
    let rec naiveEval e = 
        match e with 
        | Input f          -> try 
                                  let v = Channel.receiveFromChannel Channel.queue
                                  printfn "Received message: '%s'" v
                                  naiveEval(f v)
                              with 
                              | :? InvalidOperationException -> ()
        | Output(v, f)     -> Channel.sendToChannel Channel.queue v
                              printfn "Sent message: '%s'" v
                              naiveEval(f ())
        | Parallel(e1, e2) -> async {
                                naiveEval(e1)
                              } |> Async.Start
                              naiveEval(e2)
        | Unit             -> ()
