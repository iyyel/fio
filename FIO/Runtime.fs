// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO
open System.Threading.Tasks
open System.Collections.Concurrent

module Runtime =

    type WorkItem<'Error, 'Result> = FIO<'Error, 'Result> * Channel<Try<'Error, 'Result>>

    type Worker(id, runtime : Runtime) =
        let workQueue = new BlockingCollection<WorkItem<obj, obj>>()

        let _ = Task.Factory.StartNew(fun () ->
            for eff, resultChan in workQueue.GetConsumingEnumerable() do
                printfn $"Worker(%s{id}): Found new work. Starting work..."
                let result = runtime.Interpret eff
                printfn $"Worker(%s{id}): Work done. Sending result..."
                resultChan.Send(result))

        member _.Id = id
        member _.AddWork(workItem) =
            workQueue.Add(workItem)
            printfn $"Worker(%s{id}): Added new work. New total work queue size: %i{workQueue.Count}"

    and WorkerHandler(workerCount : int option, runtime : Runtime) as self =
        let mutable workers : Worker list = []
        do 
            workers <- self.CreateWorkers

        member public _.Workers
            with get() = workers

        member private _.CreateWorkers = let createWorkers count = 
                                            List.map (fun id -> Worker(id.ToString(), runtime)) [0..count]
                                         match workerCount with
                                         | Some count -> createWorkers (count - 1)
                                         | _          -> let cpuCount = System.Environment.ProcessorCount 
                                                         createWorkers (cpuCount - 1)

    and [<AbstractClass>] Runtime() =
        abstract Run : FIO<'Error, 'Result> -> Fiber<'Error, 'Result>
        abstract Interpret : FIO<'Error, 'Result> -> Try<'Error, 'Result>

// 
// Naive runtime implementation
// 
    and Naive() =
        inherit Runtime()
        override this.Run (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
            new NaiveFiber<'Error, 'Result>(eff, this.Interpret)

        override this.Interpret (eff : FIO<'Error, 'Result>) : Try<'Error, 'Result> =
            eff.Accept({
                new Visitor with
                    member _.VisitInput<'Error, 'Result>(input : Input<'Error, 'Result>) =
                        Success <| input.Chan.Receive()

                    member _.VisitAction<'Error, 'Result>(action : Action<'Error, 'Result>) =
                        Success <| action.Func ()

                    member _.VisitConcurrent<'FIOError, 'FIOResult, 'Error, 'Result>
                            (con : Concurrent<'FIOError, 'FIOResult, 'Error, 'Result>) = 
                        let fiber = new NaiveFiber<'FIOError, 'FIOResult>(con.Eff, this.Interpret)
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
                        let fiberA = new NaiveFiber<'Error, 'Result>(race.EffA, this.Interpret)
                        let fiberB = new NaiveFiber<'Error, 'Result>(race.EffB, this.Interpret)
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
    and Advanced(?workerCount) =
        inherit Runtime()

        member this.Workers = let workerHandler = new WorkerHandler(workerCount, this)
                              workerHandler.Workers

        override this.Run (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
            //let fiber = this.LowLevelRun eff
            failwith ""

         member this.LowLevelRun (eff : FIO<_, _>) : Fiber<'Error, 'Result> = 
            // create WorkItem for worker
            let resultChan = Channel<Try<_, _>>()
            let workItem = (eff, resultChan)

            // get a worker (just the head for demonstration purposes)
            let worker = List.head this.Workers
            let _ = worker.AddWork(workItem)

            // return fiber
            new AdvancedFiber<'Error, 'Result>(resultChan)
           
        override this.Interpret (eff : FIO<'Error, 'Result>) : Try<'Error, 'Result> =
            eff.Accept({
                new Visitor with
                    member _.VisitInput<'Error, 'Result>(input : Input<'Error, 'Result>) =
                        Success <| input.Chan.Receive()

                    member _.VisitAction<'Error, 'Result>(action : Action<'Error, 'Result>) =
                        Success <| action.Func ()

                    member _.VisitConcurrent<'FIOError, 'FIOResult, 'Error, 'Result>
                            (con : Concurrent<'FIOError, 'FIOResult, 'Error, 'Result>) = 
                        let fiber = new NaiveFiber<'FIOError, 'FIOResult>(con.Eff, this.Interpret)
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
                        let fiberA = new NaiveFiber<'Error, 'Result>(race.EffA, this.Interpret)
                        let fiberB = new NaiveFiber<'Error, 'Result>(race.EffB, this.Interpret)
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
