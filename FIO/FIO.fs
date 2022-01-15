namespace FSharp.FIO

open System.Collections.Concurrent

module FIO =

    type Channel<'a>() =
        let queue = ConcurrentQueue<'a>()
    
        member internal this.Send value =
                queue.Enqueue value
    
        member internal this.Receive =
                let status, value = queue.TryDequeue()
                if status then value else this.Receive
        
    type Effect<'a> =
        | Input of Channel<'a> * ('a -> Effect<'a>)
        | Output of 'a * Channel<'a> * (unit -> Effect<'a>)
        | Parallel of Effect<'a> * Effect<'a>
        | Return of 'a

    let Send(value, chan, cont) = Output(value, chan, cont)
    let Receive(chan, cont) = Input(chan, cont)
    
    let rec NaiveEval (eff : Effect<'a>) =
        match eff with 
        | Input(chan, cont)         -> let value = chan.Receive
                                       NaiveEval(cont value)
        | Output(value, chan, cont) -> chan.Send value
                                       NaiveEval(cont ())
        | Parallel(eff1, eff2)      -> async {
                                           NaiveEval eff1 |> ignore
                                       } |> Async.Start
                                       NaiveEval eff2
        | Return value              -> value