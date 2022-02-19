// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

module Runtime =

    type [<AbstractClass>] Runtime() =
        abstract Run<'Error, 'Result> : FIO<'Error, 'Result> -> Fiber<'Error, 'Result>
        abstract Interpret<'Error, 'Result> : FIO<'Error, 'Result> -> Try<'Error, 'Result>

    and Naive() =
        inherit Runtime()
        override this.Run<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
            new Fiber<'Error, 'Result>(eff, this.Interpret)

        override this.Interpret<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Try<'Error, 'Result> =
            eff.Accept({ 
                new FIOVisitor with
                    member _.VisitInput<'Error, 'Result>(input : Input<'Error, 'Result>) =
                        Success <| input.Chan.Receive()

                    member _.VisitOutput<'Error, 'Msg>(output : Output<'Error, 'Msg>) =
                        output.Chan.Send output.Msg
                        Success ()

                    member _.VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result>(con : Concurrent<'FiberError, 'FiberResult, 'Error, 'Result>) = 
                        let fiber = new Fiber<'FiberError, 'FiberResult>(con.Eff, this.Interpret)
                        this.Interpret <| con.Cont fiber

                    member _.VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>(await : Await<'FiberError, 'FiberResult, 'Error, 'Result>) =
                        this.Interpret <| (await.Cont <| await.Fiber.Await())

                    member _.VisitSequence<'FIOResult, 'Error, 'Result>(seq : Sequence<'FIOResult, 'Error, 'Result>) =
                        let fiber = new Fiber<'Error, 'FIOResult>(seq.Eff, this.Interpret)
                        let result = fiber.Await()
                        match result with
                        | Success res -> this.Interpret <| seq.Cont res
                        | Error error -> Error error

                    member _.VisitOrElse<'Error, 'Result>(orElse : OrElse<'Error, 'Result>) =
                        let fiber = new Fiber<'Error, 'Result>(orElse.Eff, this.Interpret)
                        let result = fiber.Await()
                        match result with
                        | Success res -> Success res
                        | Error _     -> let fiber = new Fiber<'Error, 'Result>(orElse.ElseEff, this.Interpret)
                                         fiber.Await()

                    member _.VisitOnError<'FIOError, 'Error, 'Result>(onError : OnError<'FIOError, 'Error, 'Result>) =
                        let fiber = new Fiber<'FIOError, 'Result>(onError.Eff, this.Interpret)
                        let result = fiber.Await()
                        match result with
                        | Success res -> Success res
                        | Error error -> let fiber = new Fiber<'Error, 'Result>(onError.Cont error, this.Interpret)
                                         fiber.Await()

                    member _.VisitRace<'Error, 'Result>(race : Race<'Error, 'Result>) =
                        let fiberA = new Fiber<'Error, 'Result>(race.EffA, this.Interpret)
                        let fiberB = new Fiber<'Error, 'Result>(race.EffB, this.Interpret)
                        let rec loop() = 
                            if fiberA.IsCompleted() then
                                // cancel and dispose of fiber B?
                                fiberA.Await()
                            else if fiberB.IsCompleted() then
                                // cancel and dispose of fiber A?
                                fiberB.Await()
                            else 
                                loop()
                        loop()

                    member _.VisitAttempt<'FIOError, 'FIOResult, 'Error, 'Result>(attempt : Attempt<'FIOError, 'FIOResult, 'Error, 'Result>) =
                        let fiber = new Fiber<'FIOError, 'FIOResult>(attempt.Eff, this.Interpret)
                        let result = fiber.Await()
                        match result with
                        | Success res -> let fiber = new Fiber<'Error, 'Result>(attempt.ContSuccess res, this.Interpret)
                                         fiber.Await()
                        | Error error -> let fiber = new Fiber<'Error, 'Result>(attempt.ContError error, this.Interpret)
                                         fiber.Await()

                    member _.VisitSucceed<'Error, 'Result>(succ : Succeed<'Error, 'Result>) =
                        Success succ.Result

                    member _.VisitFail<'Error, 'Result>(fail : Fail<'Error, 'Result>) =
                        Error fail.Error
            })
