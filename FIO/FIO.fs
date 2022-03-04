// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

module FIO =

    type Channel<'Msg>() =
        let bc = new BlockingCollection<'Msg>()
        member _.Send(msg) = bc.Add msg
        member _.Receive() = bc.Take()
        member _.Size() = bc.Count

    type Try<'Error, 'Result> =
        | Success of 'Result
        | Error of 'Error

    type InterpretFunc<'Error, 'Result> = FIO<'Error, 'Result> -> Try<'Error, 'Result>

    and Fiber<'Error, 'Result>
            (eff : FIO<'Error, 'Result>,
             interpret : InterpretFunc<'Error, 'Result>) =
        let task = Task.Factory.StartNew(fun () -> interpret eff)

        member _.Await() = 
            task.Result

        member _.IsCompleted() =
            task.IsCompleted

        member _.IsCompletedSuccessfully() =
            task.IsCompletedSuccessfully

        member _.IsFaulted() =
            task.IsFaulted

        member internal _.Task() =
            task

    and Visitor =
        abstract VisitInput<'Error, 'Result>                             : Input<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitAction<'Error, 'Result>                            : Action<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitConcurrent<'FIOError, 'FIOResult, 'Error, 'Result> : Concurrent<'FIOError, 'FIOResult, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitAwait<'FIOError, 'FIOResult, 'Error, 'Result>      : Await<'FIOError, 'FIOResult, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitSequence<'FIOResult, 'Error, 'Result>              : Sequence<'FIOResult, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitOrElse<'Error, 'Result>                            : OrElse<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitOnError<'FIOError, 'Error, 'Result>                : OnError<'FIOError, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitRace<'Error, 'Result>                              : Race<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitAttempt<'FIOError, 'FIOResult, 'Error, 'Result>    : Attempt<'FIOError, 'FIOResult, 'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitSucceed<'Error, 'Result>                           : Succeed<'Error, 'Result> -> Try<'Error, 'Result>
        abstract VisitFail<'Error, 'Result>                              : Fail<'Error, 'Result> -> Try<'Error, 'Result>

    and [<AbstractClass>] FIO<'Error, 'Result>() =
        abstract Accept<'Error, 'Result> : Visitor -> Try<'Error, 'Result>

    and Input<'Error, 'Result>(chan : Channel<'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Chan = chan
        override this.Accept(visitor) =
            visitor.VisitInput(this)

    and Action<'Error, 'Result>(func : unit -> 'Result) =
        inherit FIO<'Error, 'Result>()
        member internal _.Func = func
        override this.Accept(visitor) =
            visitor.VisitAction(this)

    and Concurrent<'FIOError, 'FIOResult, 'Error, 'Result>
            (eff : FIO<'FIOError, 'FIOResult>,
             cont : Fiber<'FIOError, 'FIOResult> -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitConcurrent(this)

    and Await<'FIOError, 'FIOResult, 'Error, 'Result>
            (fiber : Fiber<'FIOError, 'FIOResult>,
             cont : Try<'FIOError, 'FIOResult> -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Fiber = fiber
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitAwait(this)

    and Sequence<'FIOResult, 'Error, 'Result>
            (eff : FIO<'Error, 'FIOResult>,
             cont : 'FIOResult -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitSequence(this)

    and OrElse<'Error, 'Result>
            (eff : FIO<'Error, 'Result>,
             elseEff : FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.ElseEff = elseEff
        override this.Accept(visitor) =
            visitor.VisitOrElse(this)

    and OnError<'FIOError, 'Error, 'Result>
            (eff : FIO<'FIOError, 'Result>,
             cont : 'FIOError -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitOnError(this)

    and Race<'Error, 'Result>
            (effA : FIO<'Error, 'Result>,
             effB : FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.EffA = effA
        member internal _.EffB = effB
        override this.Accept(visitor) =
            visitor.VisitRace(this)

    and Attempt<'FIOError, 'FIOResult, 'Error, 'Result>
            (eff : FIO<'FIOError, 'FIOResult>,
             contSuccess : 'FIOResult -> FIO<'Error, 'Result>,
             contError : 'FIOError -> FIO<'Error, 'Result>) =
        inherit FIO<'Error, 'Result>()
        member internal _.Eff = eff
        member internal _.ContSuccess = contSuccess
        member internal _.ContError = contError
        override this.Accept(visitor) =
            visitor.VisitAttempt(this)

    and Succeed<'Error, 'Result>(result : 'Result) =
        inherit FIO<'Error, 'Result>()
        member internal _.Result = result
        override this.Accept(visitor) =
            visitor.VisitSucceed(this)

    and Fail<'Error, 'Result>(error : 'Error) =
        inherit FIO<'Error, 'Result>()
        member internal _.Error = error
        override this.Accept(visitor) =
            visitor.VisitFail(this)

    let Send<'Error, 'Result>(result : 'Result, chan : Channel<'Result>) =
        Action<'Error, unit>(fun () -> chan.Send(result))

    let Receive<'Error, 'Result>(chan : Channel<'Result>) =
        Input<'Error, 'Result>(chan)

    let End() : Succeed<'Error, unit> =
        Succeed ()

    let (>>=) (eff : FIO<'Error, 'FIOResult>) (cont : 'FIOResult -> FIO<'Error, 'Result>) =
        Sequence<'FIOResult, 'Error, 'Result>(eff, cont)

    let Parallel<'ErrorA, 'ResultA, 'ErrorB, 'ResultB, 'ErrorC, 'ResultC>
            (effA : FIO<'ErrorA, 'ResultA>,
             effB : FIO<'ErrorB, 'ResultB>) =
        Concurrent(effA, fun fiberA ->
            Concurrent(effB, fun fiberB ->
                Await(fiberA, fun resA ->
                    Await(fiberB, fun resB ->
                        Succeed ((resA, resB))))))
