// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO
open System.Threading.Tasks

module Runtime =

    type Worker(id, interpret : InterpretFunc<'Error, 'Result>) =
        member _.Id = id
        member _.Execute(eff) = 
            Task.Factory.StartNew(fun () -> interpret eff)
 
    [<AbstractClass>]
    type Runtime() =
        abstract Run<'Error, 'Result> : FIO<'Error, 'Result> -> Fiber<'Error, 'Result>
        abstract Interpret<'Error, 'Result> : FIO<'Error, 'Result> -> Try<'Error, 'Result>

// 
// Naive runtime implementation
// 
    and Naive() =
        inherit Runtime()
        override this.Run<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
            new Fiber<'Error, 'Result>(eff, this.Interpret)

        override this.Interpret<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Try<'Error, 'Result> =
            eff.Accept({
                new Visitor with
                    member _.VisitInput<'Error, 'Result>(input : Input<'Error, 'Result>) =
                        Success <| input.Chan.Receive()

                    member _.VisitAction<'Error, 'Result>(action : Action<'Error, 'Result>) =
                        Success <| action.Func ()

                    member _.VisitConcurrent<'FIOError, 'FIOResult, 'Error, 'Result>
                            (con : Concurrent<'FIOError, 'FIOResult, 'Error, 'Result>) = 
                        let fiber = new Fiber<'FIOError, 'FIOResult>(con.Eff, this.Interpret)
                        this.Interpret <| con.Cont fiber

                    member _.VisitAwait<'FIOError, 'FIOResult, 'Error, 'Result>
                            (await : Await<'FIOError, 'FIOResult, 'Error, 'Result>) =
                        this.Interpret <| (await.Cont <| await.Fiber.Await())

                    member _.VisitSequence<'FIOResult, 'Error, 'Result>
                            (seq : Sequence<'FIOResult, 'Error, 'Result>) =
                        let result = this.Interpret <| seq.Eff
                        match result with
                        | Success res -> this.Interpret <| seq.Cont res
                        | Error error -> Error error

                    member _.VisitOrElse<'Error, 'Result>(orElse : OrElse<'Error, 'Result>) =
                        let result = this.Interpret <| orElse.Eff
                        match result with
                        | Success res -> Success res
                        | Error _     -> this.Interpret <| orElse.ElseEff

                    member _.VisitOnError<'FIOError, 'Error, 'Result>
                            (onError : OnError<'FIOError, 'Error, 'Result>) =
                        let result = this.Interpret <| onError.Eff
                        match result with
                        | Success res -> Success res
                        | Error error -> this.Interpret <| onError.Cont error

                    member _.VisitRace<'Error, 'Result>(race : Race<'Error, 'Result>) =
                        let fiberA = new Fiber<'Error, 'Result>(race.EffA, this.Interpret)
                        let fiberB = new Fiber<'Error, 'Result>(race.EffB, this.Interpret)
                        let task = Task.WhenAny([fiberA.Task(); fiberB.Task()])
                        task.Result.Result

                    member _.VisitAttempt<'FIOError, 'FIOResult, 'Error, 'Result>
                            (attempt : Attempt<'FIOError, 'FIOResult, 'Error, 'Result>) =
                        let result = this.Interpret <| attempt.Eff
                        match result with
                        | Success res -> this.Interpret <| attempt.ContSuccess res
                        | Error error -> this.Interpret <| attempt.ContError error

                    member _.VisitSucceed<'Error, 'Result>(succ : Succeed<'Error, 'Result>) =
                        Success succ.Result

                    member _.VisitFail<'Error, 'Result>(fail : Fail<'Error, 'Result>) =
                        Error fail.Error
            })

// 
// Advanced runtime implementation
// 
    and Advanced(?workerCount) as self =
        inherit Runtime()
        let mutable workers = []
        do 
            workers <- self.CreateWorkers

        member public _.Workers
            with get() = workers

        member private this.CreateWorkers = let createWorkers count = 
                                                List.map (fun id -> Worker(id.ToString(), this.Interpret)) [0..count]
                                            match workerCount with
                                            | Some count -> createWorkers (count - 1)
                                            | _          -> let cpuCount = System.Environment.ProcessorCount 
                                                            createWorkers (cpuCount - 1)

        override this.Run<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
            new Fiber<'Error, 'Result>(eff, this.Interpret)

        override this.Interpret<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Try<'Error, 'Result> =
            eff.Accept({
                new Visitor with
                    member _.VisitInput<'Error, 'Result>(input : Input<'Error, 'Result>) =
                        Success <| input.Chan.Receive()

                    member _.VisitAction<'Error, 'Result>(action : Action<'Error, 'Result>) =
                        Success <| action.Func ()

                    member _.VisitConcurrent<'FIOError, 'FIOResult, 'Error, 'Result>
                            (con : Concurrent<'FIOError, 'FIOResult, 'Error, 'Result>) = 
                        let fiber = new Fiber<'FIOError, 'FIOResult>(con.Eff, this.Interpret)
                        this.Interpret <| con.Cont fiber

                    member _.VisitAwait<'FIOError, 'FIOResult, 'Error, 'Result>
                            (await : Await<'FIOError, 'FIOResult, 'Error, 'Result>) =
                        this.Interpret <| (await.Cont <| await.Fiber.Await())

                    member _.VisitSequence<'FIOResult, 'Error, 'Result>
                            (seq : Sequence<'FIOResult, 'Error, 'Result>) =
                        let result = this.Interpret <| seq.Eff
                        match result with
                        | Success res -> this.Interpret <| seq.Cont res
                        | Error error -> Error error

                    member _.VisitOrElse<'Error, 'Result>(orElse : OrElse<'Error, 'Result>) =
                        let result = this.Interpret <| orElse.Eff
                        match result with
                        | Success res -> Success res
                        | Error _     -> this.Interpret <| orElse.ElseEff

                    member _.VisitOnError<'FIOError, 'Error, 'Result>
                            (onError : OnError<'FIOError, 'Error, 'Result>) =
                        let result = this.Interpret <| onError.Eff
                        match result with
                        | Success res -> Success res
                        | Error error -> this.Interpret <| onError.Cont error

                    member _.VisitRace<'Error, 'Result>(race : Race<'Error, 'Result>) =
                        let fiberA = new Fiber<'Error, 'Result>(race.EffA, this.Interpret)
                        let fiberB = new Fiber<'Error, 'Result>(race.EffB, this.Interpret)
                        let task = Task.WhenAny([fiberA.Task(); fiberB.Task()])
                        task.Result.Result

                    member _.VisitAttempt<'FIOError, 'FIOResult, 'Error, 'Result>
                            (attempt : Attempt<'FIOError, 'FIOResult, 'Error, 'Result>) =
                        let result = this.Interpret <| attempt.Eff
                        match result with
                        | Success res -> this.Interpret <| attempt.ContSuccess res
                        | Error error -> this.Interpret <| attempt.ContError error

                    member _.VisitSucceed<'Error, 'Result>(succ : Succeed<'Error, 'Result>) =
                        Success succ.Result

                    member _.VisitFail<'Error, 'Result>(fail : Fail<'Error, 'Result>) =
                        Error fail.Error
            })

    and Default() =
        inherit Advanced()
