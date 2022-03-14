// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

open System.Threading.Tasks
open System.Collections.Concurrent

module Runtime =
    
    type WorkItem = { Eff: FIO<obj, obj>; Chan: BlockingCollection<Result<obj, obj>> } with
        static member Create(eff) = { Eff = eff; Chan = new BlockingCollection<Result<obj, obj>>() }
        member internal this.Await() = this.Chan.Take()
        member internal this.Completed() = this.Chan.Count > 0

    and Worker(id, runtime : Runtime, workQueue : BlockingCollection<WorkItem>) =
        let _ = new BlockingCollection<WorkItem>()
        let _ = Task.Factory.StartNew(fun () ->
            for workItem in workQueue.GetConsumingEnumerable() do
                #if DEBUG
                printfn $"DEBUG: Worker(%s{id}): Found new work. Starting work..."
                #endif
                let res = runtime.LowLevelEval workItem.Eff
                #if DEBUG
                printfn $"DEBUG: Worker(%s{id}): Work done. Sending result..."
                #endif
                workItem.Chan.Add res)
        member _.Id = id
        member _.AddWork(workItem) =
            workQueue.Add(workItem)
            #if DEBUG
            printfn $"DEBUG: Worker(%s{id}): Got new work. New work queue size: %i{workQueue.Count}"
            #endif

    and [<AbstractClass>] Runtime() =
        abstract Eval : FIO<'R, 'E> -> Fiber<'R, 'E>
        abstract LowLevelEval : FIO<obj, obj> -> Result<obj, obj>
        
    and Naive() =
        inherit Runtime()

        override this.LowLevelEval (eff : FIO<obj, obj>) : Result<obj, obj> =
            match eff with
            | NonBlocking action               -> action()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (eff, fiber, llfiber) -> let _ = Task.Factory.StartNew(fun () -> llfiber.Complete <| this.LowLevelEval eff)
                                                  Ok fiber
            | Await llfiber                    -> llfiber.Await()
            | Sequence (eff, cont)             -> match this.LowLevelEval eff with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | Success res                      -> Ok res
            | Failure err                      -> Error err

        override this.Eval (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            let _ = Task.Factory.StartNew(fun () -> fiber.ToLowLevel().Complete <| this.LowLevelEval (eff.Upcast()))
            fiber

    and Advanced(?workerCount) as self =
        inherit Runtime()

        let createWorkers workQueue =
            let create count = List.map (fun id -> Worker(id.ToString(), self, workQueue)) [0..count - 1]
            match workerCount with
            | Some count -> create count
            | _          -> create System.Environment.ProcessorCount
        
        let workQueue = new BlockingCollection<WorkItem>()
        let workers = createWorkers workQueue

        override this.LowLevelEval (eff : FIO<obj, obj>) : Result<obj, obj> =
            match eff with
            | NonBlocking action               -> action()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (eff, fiber, llfiber) -> let _ = Task.Factory.StartNew(fun () -> llfiber.Complete <| this.LowLevelEval eff)
                                                  Ok fiber
            | Await llfiber                    -> llfiber.Await()
            | Sequence (eff, cont)             -> match this.LowLevelEval eff with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | Success res                      -> Ok res
            | Failure err                      -> Error err

        override _.Eval (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let workItem = WorkItem.Create <| eff.Upcast()
            workQueue.Add workItem
            let fiber = Fiber<'R, 'e>()
            let _ = Task.Factory.StartNew(fun () -> fiber.ToLowLevel().Complete <| workItem.Chan.Take())
            fiber
