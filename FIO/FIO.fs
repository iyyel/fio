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

    type InterpretFunc<'Result, 'Error> = FIO<'Result, 'Error> -> Result<'Result, 'Error>

    and Fiber<'Result, 'Error>
            (eff : FIO<'Result, 'Error>,
             interpret : InterpretFunc<'Result, 'Error>) =
        let task = Task.Factory.StartNew(fun () -> interpret eff)

        member _.Await() = 
            task.Result

        member internal _.Task() =
            task

    and Visitor =
        abstract VisitInput<'Result, 'Error>                             : Input<'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitAction<'Result, 'Error>                            : Action<'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitConcurrent<'FIOResult, 'FIOError, 'Result, 'Error> : Concurrent<'FIOResult, 'FIOError, 'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitAwait<'FIOResult, 'FIOError, 'Result, 'Error>      : Await<'FIOResult, 'FIOError, 'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitSequence<'FIOResult, 'Result, 'Error>              : Sequence<'FIOResult, 'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitOrElse<'Result, 'Error>                            : OrElse<'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitOnError<'FIOError,'Result, 'Error>                 : OnError<'FIOError,'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitRace<'Result, 'Error>                              : Race<'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitAttempt<'FIOResult, 'FIOError, 'Result, 'Error>    : Attempt<'FIOResult, 'FIOError, 'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitSucceed<'Result, 'Error>                           : Succeed<'Result, 'Error> -> Result<'Result, 'Error>
        abstract VisitFail<'Result, 'Error>                              : Fail<'Result, 'Error> -> Result<'Result, 'Error>

    and [<AbstractClass>] FIO<'Result, 'Error>() =
        abstract Accept<'Result, 'Error> : Visitor -> Result<'Result, 'Error>

    and Input<'Result, 'Error>(chan : Channel<'Result>) =
        inherit FIO<'Result, 'Error>()
        member internal _.Chan = chan
        override this.Accept(visitor) =
            visitor.VisitInput(this)

    and Action<'Result, 'Error>(func : unit -> 'Result) =
        inherit FIO<'Result, 'Error>()
        member internal _.Func = func
        override this.Accept(visitor) =
            visitor.VisitAction(this)

    and Concurrent<'FIOResult, 'FIOError, 'Result, 'Error>
            (eff : FIO<'FIOResult, 'FIOError>,
             cont : Fiber<'FIOResult, 'FIOError> -> FIO<'Result, 'Error>) =
        inherit FIO<'Result, 'Error>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitConcurrent(this)

    and Await<'FIOResult, 'FIOError, 'Result, 'Error>
            (fiber : Fiber<'FIOResult, 'FIOError>,
             cont : Result<'FIOResult, 'FIOError> -> FIO<'Result, 'Error>) =
        inherit FIO<'Result, 'Error>()
        member internal _.Fiber = fiber
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitAwait(this)

    and Sequence<'FIOResult, 'Result, 'Error>
            (eff : FIO<'FIOResult, 'Error>,
             cont : 'FIOResult -> FIO<'Result, 'Error>) =
        inherit FIO<'Result, 'Error>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitSequence(this)

    and OrElse<'Result, 'Error>
            (eff : FIO<'Result, 'Error>,
             elseEff : FIO<'Result, 'Error>) =
        inherit FIO<'Result, 'Error>()
        member internal _.Eff = eff
        member internal _.ElseEff = elseEff
        override this.Accept(visitor) =
            visitor.VisitOrElse(this)

    and OnError<'FIOError, 'Result, 'Error>
            (eff : FIO<'Result, 'FIOError>,
             cont : 'FIOError -> FIO<'Result, 'Error>) =
        inherit FIO<'Result, 'Error>()
        member internal _.Eff = eff
        member internal _.Cont = cont
        override this.Accept(visitor) =
            visitor.VisitOnError(this)

    and Race<'Result, 'Error>
            (effA : FIO<'Result, 'Error>,
             effB : FIO<'Result, 'Error>) =
        inherit FIO<'Result, 'Error>()
        member internal _.EffA = effA
        member internal _.EffB = effB
        override this.Accept(visitor) =
            visitor.VisitRace(this)

    and Attempt<'FIOResult, 'FIOError, 'Result, 'Error>
            (eff : FIO<'FIOResult, 'FIOError>,
             contSuccess : 'FIOResult -> FIO<'Result, 'Error>,
             contError : 'FIOError -> FIO<'Result, 'Error>) =
        inherit FIO<'Result, 'Error>()
        member internal _.Eff = eff
        member internal _.ContSuccess = contSuccess
        member internal _.ContError = contError
        override this.Accept(visitor) =
            visitor.VisitAttempt(this)

    and Succeed<'Result, 'Error>(result : 'Result) =
        inherit FIO<'Result, 'Error>()
        member internal _.Result = result
        override this.Accept(visitor) =
            visitor.VisitSucceed(this)

    and Fail<'Result, 'Error>(error : 'Error) =
        inherit FIO<'Result, 'Error>()
        member internal _.Error = error
        override this.Accept(visitor) =
            visitor.VisitFail(this)

    let Send<'Result, 'Error>(result : 'Result, chan : Channel<'Result>) =
        Action<unit, 'Error>(fun () -> chan.Send(result))

    let Receive<'Result, 'Error>(chan : Channel<'Result>) =
        Input<'Result, 'Error>(chan)

    let End() : Succeed<unit, 'Error> =
        Succeed ()

    let (>>=) (eff : FIO<'FIOResult, 'Error>) (cont : 'FIOResult -> FIO<'Result, 'Error>) =
        Sequence<'FIOResult, 'Result, 'Error>(eff, cont)

    let Parallel<'ResultA, 'ErrorA, 'ResultB, 'ErrorB, 'ResultC, 'ErrorC>
            (effA : FIO<'ResultA, 'ErrorA>,
             effB : FIO<'ResultB, 'ErrorB>) =
        Concurrent(effA, fun fiberA ->
            Concurrent(effB, fun fiberB ->
                Await(fiberA, fun resA ->
                    Await(fiberB, fun resB ->
                        Succeed ((resA, resB))))))
