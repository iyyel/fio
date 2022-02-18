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

    type Try<'Error, 'Result> =
        | Success of 'Result
        | Error of 'Error

    type Fiber<'Error, 'Result>(eff : FIO<'Error, 'Result>, interpret : FIO<'Error, 'Result> -> Try<'Error, 'Result>) =
        let task = Task.Factory.StartNew(fun () -> interpret eff)
        member _.Await() = task.Result 
        //member _.Try(contError : 'Error -> FIO<'ErrorA, 'ResultA>, contSuccess : 'Result -> FIO<'ErrorB, 'ResultB>) = () // continuation for error and success, will be executed depending on the current effects (Eff) result (i.e. error or result).
        member _.IsCompleted() = task.IsCompleted
        member _.IsCompletedSuccessfully() = task.IsCompletedSuccessfully
        member _.IsFaulted() = task.IsFaulted

    and FIOVisitor =
        abstract VisitInput<'Error, 'Result>                                 : Input<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitOutput<'Error, 'Msg>                                   : Output<'Error, 'Msg> -> Try<'Error, unit>
        abstract VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result> : Concurrent<'FiberError, 'FiberResult, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>      : Await<'FiberError, 'FiberResult, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitSequence<'FIOResult, 'Error, 'Result>                  : Sequence<'FIOResult, 'Error, 'Result> -> Try<'Error, 'Result> 
        abstract VisitOrElse<'Error, 'Result>                                : OrElse<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitOnError<'FIOError, 'Error, 'Result>                    : OnError<'FIOError, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitRace<'Error, 'Result>                                  : Race<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitAttempt<'FIOError, 'FIOResult, 'Error, 'Result>        : Attempt<'FIOError, 'FIOResult, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitSucceed<'Error, 'Result>                               : Succeed<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitFail<'Error, 'Result>                                  : Fail<'Error, 'Result> -> Try<'Error, 'Result>

    and [<AbstractClass>] FIO<'Error, 'Result>() =
        abstract Accept<'Error, 'Result> : FIOVisitor -> Try<'Error, 'Result>

    and Input<'Error, 'Result>(chan : Channel<'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Chan = chan
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitInput<'Error, 'Result>(this)

    and Output<'Error, 'Msg>
            (msg : 'Msg,
             chan : Channel<'Msg>) =
        inherit FIO<'Error, unit>()
        member internal _.Msg = msg
        member internal _.Chan = chan
        override this.Accept<'Error, 'Resullt>(visitor) =
            visitor.VisitOutput<'Error, 'Msg>(this)

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
             cont : Try<'FiberError, 'FiberResult> -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Fiber = fiber
        member internal _.Cont = cont
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>(this)

    and Sequence<'FIOResult, 'Error, 'Result>
            (eff : FIO<'Error, 'FIOResult>,
             cont : 'FIOResult -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitSequence<'FIOResult, 'Error, 'Result>(this)

    and OrElse<'Error, 'Result>
            (eff : FIO<'Error, 'Result>,
             elseEff : FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.ElseEff = elseEff
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitOrElse<'Error, 'Result>(this)

    and OnError<'FIOError, 'Error, 'Result>
            (eff : FIO<'FIOError, 'Result>,
             cont : 'FIOError -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitOnError<'FIOError, 'Error, 'Result>(this)

    and Race<'Error, 'Result>
            (effA : FIO<'Error, 'Result>,
             effB : FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.EffA = effA
        member internal _.EffB = effB
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitRace<'Error, 'Result>(this)

    and Attempt<'FIOError, 'FIOResult, 'Error, 'Result>
            (eff : FIO<'FIOError, 'FIOResult>,
             contSuccess : 'FIOResult -> FIO<'Error, 'Result>,
             contError : 'FIOError -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.ContSuccess = contSuccess
        member internal _.ContError = contError
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitAttempt<'FIOError, 'FIOResult, 'Error, 'Result>(this)

    and Succeed<'Error, 'Result>(value : 'Result) =
        inherit FIO<'Error, 'Result>()
        member internal _.Value = value
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitSucceed<'Error, 'Result>(this)

    and Fail<'Error, 'Result>(err : 'Error) =
        inherit FIO<'Error, 'Result>()
        member internal _.Error = err
        override this.Accept<'Error, 'Result>(visitor) =
            visitor.VisitFail<'Error, 'Result>(this)

    let (>>=) (eff : FIO<'Error, 'FIOResult>) (cont : 'FIOResult -> FIO<'Error, 'Result>) =
        Sequence(eff, cont)

    let Send<'Error, 'Result>(value : 'Result, chan : Channel<'Result>) =
        Output(value, chan)

    let Receive<'Error, 'Result>(chan : Channel<'Result>) =
        Input(chan)

    let Parallel<'ErrorA, 'ResultA, 'ErrorB, 'ResultB, 'ErrorC, 'ResultC>
        (effA : FIO<'ErrorA, 'ResultA>,
            effB : FIO<'ErrorB, 'ResultB>) =
        Concurrent(effA, fun fiberA ->
            Concurrent(effB, fun fiberB ->
                Await(fiberA, fun resA ->
                    Await(fiberB, fun resB -> 
                        Succeed ((resA, resB))))))

    let End() : Succeed<'Error, unit> =
        Succeed ()