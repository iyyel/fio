// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

module FIO =

    type Channel<'Msg>() =
        let bc = new BlockingCollection<'Msg>()
        member _.Send value = bc.Add value
        member _.Receive() = bc.Take()

    type Fiber<'Error, 'Result>(eff : FIO<'Error, 'Result>, interpret : FIO<'Error, 'Result> -> 'Result) =
        let task = Task.Factory.StartNew(fun () -> interpret eff)
        member _.Await() = task.Result
        member _.OrElse(effA : FIO<'ErrorA, 'ResultA>) = () // try this effect (Eff), however, if it fails, then try another one (EffA).
        member _.CatchAll(cont : 'Error -> FIO<'ErrorA, 'ResultA>) = () // catch all errors and recover from it effectfully. 
        member _.Race(effA : FIO<'ErrorA, 'ResultA>) = () // return the result of whichever effect completes first
        member _.Try(contError : 'Error -> FIO<'ErrorA, 'ResultA>, contSuccess : 'Result -> FIO<'ErrorB, 'ResultB>) = () // continuation for error and success, will be executed depending on the current effects (Eff) result (i.e. error or result).
        member _.IsCompleted() = task.IsCompleted
        member _.IsCompletedSuccessfully() = task.IsCompletedSuccessfully
        member _.IsFaulted() = task.IsFaulted

    and FIOVisitor =
        abstract VisitInput<'Msg, 'Error, 'Result>                           : Input<'Msg, 'Error, 'Result> -> 'Result
        abstract VisitOutput<'Msg, 'Error, 'Result>                          : Output<'Msg, 'Error, 'Result> -> 'Result
        abstract VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result> : Concurrent<'FiberError, 'FiberResult, 'Error, 'Result> -> 'Result
        abstract VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>      : Await<'FiberError, 'FiberResult, 'Error, 'Result> -> 'Result
        abstract VisitSucceed<'Error, 'Result>                               : Succeed<'Error, 'Result> -> 'Result
        abstract VisitFail<'Error, 'Result>                                  : Fail<'Error, 'Result> -> 'Result

    and [<AbstractClass>] FIO<'Error, 'Result>() =
        abstract Accept<'Error, 'Result> : FIOVisitor -> 'Result

    and Input<'Msg, 'Error, 'Result>
            (chan : Channel<'Msg>,
             cont : 'Msg -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Chan = chan
        member internal _.Cont = cont
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitInput<'Msg, 'Error, 'Result>(this)

    and Output<'Msg, 'Error, 'Result>
            (value : 'Msg,
              chan : Channel<'Msg>,
              cont : unit -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Value = value
        member internal _.Chan = chan
        member internal _.Cont = cont
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitOutput<'Msg, 'Error, 'Result>(this)

    and Concurrent<'FiberError, 'FiberResult, 'Error, 'Result>
            (eff : FIO<'FiberError, 'FiberResult>,
            cont : Fiber<'FiberError, 'FiberResult> -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result>(this)

    and Await<'FiberError, 'FiberResult, 'Error, 'Result>
            (fiber : Fiber<'FiberError, 'FiberResult>,
              cont : 'FiberResult -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Fiber = fiber
        member internal _.Cont = cont
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>(this)

    and Succeed<'Error, 'Result>(value : 'Result) =
        inherit FIO<'Error, 'Result>()
        member internal _.Value = value
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitSucceed<'Error, 'Result>(this)

    and Fail<'Error, 'Result>(error : 'Result) =
        inherit FIO<'Error, 'Result>()
        member internal _.Error = error
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitFail<'Error, 'Result>(this)

    let Send<'Msg, 'Error, 'Result>
        (value : 'Msg,
          chan : Channel<'Msg>,
          cont : unit -> FIO<'Error, 'Result>)
               : Output<'Msg, 'Error, 'Result> =
        Output(value, chan, cont)

    let Receive<'Msg, 'Error, 'Result>
        (chan : Channel<'Msg>,
         cont : 'Msg -> FIO<'Error, 'Result>)
              : Input<'Msg, 'Error, 'Result> =
        Input(chan, cont)

    let Parallel<'ErrorA, 'ResultA, 'ErrorB, 'ResultB, 'ErrorC, 'ResultC>
        (effA : FIO<'ErrorA, 'ResultA>,
         effB : FIO<'ErrorB, 'ResultB>,
         cont : 'ResultA * 'ResultB -> FIO<'ErrorC, 'ResultC>) =
        Concurrent(effA, fun taskA ->
            Concurrent(effB, fun taskB ->
                Await(taskA, fun resA ->
                    Await(taskB, fun resB ->
                        cont (resA, resB)))))

    let End() : Succeed<unit, unit> =
        Succeed ()