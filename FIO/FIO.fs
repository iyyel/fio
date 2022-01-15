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
        | Parallel of Effect<'a> * Effect<'a> * ('a * 'a -> Effect<'a>)
        | Return of 'a

    let Send(value, chan, cont) = Output(value, chan, cont)
    let Receive(chan, cont) = Input(chan, cont)
    
    let rec NaiveEval (eff : Effect<'a>) : 'a =
        match eff with 
        | Input(chan, cont)          -> let value = chan.Receive
                                        NaiveEval <| cont value
        | Output(value, chan, cont)  -> chan.Send value
                                        NaiveEval <| cont ()
        | Parallel(eff1, eff2, cont) -> let task = Async.AwaitTask <| Async.StartAsTask (ParallelWork eff1 eff2)
                                        let (r1, r2) = Async.RunSynchronously task
                                        NaiveEval <| cont (r1, r2)
        | Return value               -> value
    and ParallelWork eff1 eff2 = 
        async {
            let workEff1 = async {
                return NaiveEval eff1
            }
            let workEff2 = async {
                return NaiveEval eff2
            }
            let! result = Async.Parallel <| Seq.ofList [workEff1; workEff2]
            return (result[0], result[1])
        }