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

    [<AbstractClass>]
    type Effect<'Result>() =
        class end
    and Input<'Result>(chan : Channel<'Result>, cont : 'Result -> Effect<'Result>) = 
        inherit Effect<'Result>()
        member internal this.Chan = chan
        member internal this.Cont = cont
    and Output<'Result>(value : 'Result, chan : Channel<'Result>, cont : unit -> Effect<'Result>) =
        inherit Effect<'Result>()
        member internal this.Value = value
        member internal this.Chan = chan
        member internal this.Cont = cont
    and Concurrent<'Result, 'Future>(eff: Effect<'Future>, cont: Async<'Future> -> Effect<'Result>) = 
        inherit Effect<'Result>()
        member internal this.Eff = eff
        member internal this.Cont = cont
    and Await<'Result, 'Future>(future: Async<'Future>, cont: 'Future -> Effect<'Result>) =
        inherit Effect<'Result>()
        member internal this.Future = future
        member internal this.Cont = cont
    and Return<'Result>(value : 'Result) =
        inherit Effect<'Result>()
        member internal this.Value = value

    let Send(value, chan, cont) = Output(value, chan, cont)
    let Receive(chan, cont) = Input(chan, cont)
    
    let rec NaiveEval (eff : Effect<_>) : _ =
        match eff with
        | :? Input<'Result> as input             -> let value = input.Chan.Receive
                                                    NaiveEval <| input.Cont value
        | :? Output<'Result> as output           -> output.Chan.Send output.Value
                                                    NaiveEval <| output.Cont ()
        | :? Concurrent<'Result, 'Future> as con -> let work = async {
                                                        return NaiveEval con.Eff
                                                    }
                                                    let task = Async.AwaitTask <| Async.StartAsTask work
                                                    NaiveEval <| con.Cont task
        | :? Await<'Result, 'Future> as await    -> let res = Async.RunSynchronously await.Future
                                                    NaiveEval <| await.Cont res
        | :? Return<'Result> as ret              -> ret.Value
        | _                                      -> failwith "Unsupported effect!"
