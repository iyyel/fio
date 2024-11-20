module FIO.Runtime.Advanced

open FIO.Core
open FIO.Runtime.Core

#if DETECT_DEADLOCK || MONITOR
open FIO.Monitor
#endif

open System.Collections.Concurrent

        
        type internal EvalWorker(
            runtime: Runtime,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorker: BlockingWorker,
            #if DETECT_DEADLOCK
            deadlockDetector: DeadlockDetector<BlockingWorker, EvalWorker>,
            #endif
            evalSteps) as self =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for workItem in workItemQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    match runtime.LowLevelRun workItem.Eff workItem.PrevAction evalSteps workItem.Stack with
                    | (Success res, _), Evaluated, _ ->
                        self.CompleteWorkItem workItem (Ok res)
                    | (Failure err, _), Evaluated, _ ->
                        self.CompleteWorkItem workItem (Error err)
                    | (eff, stack), RescheduleForRunning, _ ->
                        let workItem = WorkItem.Create eff stack workItem.IFiber RescheduleForRunning
                        workItemQueue.Add workItem
                    | (eff, stack), RescheduleForBlocking blockingItem, _ ->
                        let workItem = WorkItem.Create eff stack workItem.IFiber (RescheduleForBlocking blockingItem)
                        blockingWorker.RescheduleForBlocking blockingItem workItem
                        self.HandleBlockingFiber blockingItem
                    | _ -> failwith $"EvalWorker: Error occurred while evaluating effect!"
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
            } |> Async.StartAsTask |> ignore)

            member private _.CompleteWorkItem workItem res =
                workItem.Complete res
                workItem.IFiber.RescheduleBlockingWorkItems workItemQueue
                #if DETECT_DEADLOCK
                deadlockDetector.RemoveBlockingItem (BlockingFiber workItem.IFiber)
                #endif

            member private _.HandleBlockingFiber blockingItem =
                match blockingItem with
                | BlockingFiber ifiber ->
                    if ifiber.Completed() then
                        ifiber.RescheduleBlockingWorkItems workItemQueue
                | _ -> ()

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif
            
        and internal BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            #if DETECT_DEADLOCK
            deadlockDetector: DeadlockDetector<BlockingWorker, EvalWorker>,
            #endif
            blockingEventQueue: BlockingCollection<Channel<obj>>) =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for blockingChan in blockingEventQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    if blockingChan.HasBlockingWorkItems() then
                        blockingChan.RescheduleBlockingWorkItem workItemQueue
                        #if DETECT_DEADLOCK
                        deadlockDetector.RemoveBlockingItem (BlockingChannel blockingChan)
                        #endif
                    else
                        blockingEventQueue.Add blockingChan
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
            } |> Async.StartAsTask |> ignore)

            member internal _.RescheduleForBlocking blockingItem workItem =
                match blockingItem with
                | BlockingChannel chan ->
                    chan.AddBlockingWorkItem workItem
                | BlockingFiber ifiber ->
                    ifiber.AddBlockingWorkItem workItem
                #if DETECT_DEADLOCK
                deadlockDetector.AddBlockingItem blockingItem
                #endif

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif

        and Runtime(
            evalWorkerCount,
            blockingWorkerCount,
            evalStepCount) as self =
            inherit Runner()

            let workItemQueue = new BlockingCollection<WorkItem>()
            let blockingEventQueue = new BlockingCollection<Channel<obj>>()

            #if DETECT_DEADLOCK
            let deadlockDetector = new DeadlockDetector<BlockingWorker, EvalWorker>(workItemQueue, 500)
            #endif

            do let blockingWorkers = self.CreateBlockingWorkers()
               self.CreateEvalWorkers (List.head blockingWorkers) |> ignore
               #if DETECT_DEADLOCK
               let evalWorkers = self.CreateEvalWorkers (List.head blockingWorkers)
               deadlockDetector.SetBlockingWorkers blockingWorkers
               deadlockDetector.SetEvalWorkers evalWorkers
               #endif
               #if MONITOR
               Monitor(workItemQueue, None, Some blockingEventQueue, None)
               |> ignore
               #endif

            new() = Runtime(System.Environment.ProcessorCount - 1, 1, 15)

            member internal this.LowLevelRun eff
                prevAction evalSteps
                (stack : List<StackFrame>)
                : (FIO<obj, obj> * List<StackFrame>) * Action * int =

                let rec handleSuccess res newEvalSteps stack =
                    match stack with
                    | [] -> ((Success res, []), Evaluated, newEvalSteps)
                    | s::ss ->
                        match s with
                        | SuccHandler succCont ->
                            this.LowLevelRun (succCont res) Evaluated evalSteps ss
                        | ErrorHandler _ ->
                            handleSuccess res newEvalSteps ss
                    
                let rec handleError err newEvalSteps stack =
                    match stack with
                    | [] -> ((Failure err, []), Evaluated, newEvalSteps)
                    | s::ss ->
                        match s with
                        | SuccHandler _ ->
                            handleError err newEvalSteps ss
                        | ErrorHandler errCont ->
                            this.LowLevelRun (errCont err) Evaluated evalSteps ss

                let handleResult result newEvalSteps stack =
                    match result with
                    | Ok res -> handleSuccess res newEvalSteps stack
                    | Error err -> handleError err newEvalSteps stack
      
                if evalSteps = 0 then
                    ((eff, stack), RescheduleForRunning, 0)
                else
                    let newEvalSteps = evalSteps - 1
                    match eff with
                    | NonBlocking action ->
                        handleResult (action ()) newEvalSteps stack
                    | Blocking chan ->
                        if prevAction = RescheduleForBlocking (BlockingChannel chan) then
                            let res = chan.Take()
                            handleSuccess res newEvalSteps stack
                        else
                            ((Blocking chan, stack),
                                RescheduleForBlocking (BlockingChannel chan), evalSteps)
                    | SendMessage (value, chan) ->
                        chan.Add value
                        blockingEventQueue.Add <| chan
                        handleSuccess value newEvalSteps stack
                    | Concurrent (eff, fiber, ifiber) ->
                        workItemQueue.Add <| WorkItem.Create eff [] ifiber prevAction
                        handleSuccess fiber newEvalSteps stack
                    | AwaitFiber ifiber ->
                        if ifiber.Completed() then
                            handleResult (ifiber.Await()) newEvalSteps stack
                        else
                            ((AwaitFiber ifiber, stack),
                                RescheduleForBlocking (BlockingFiber ifiber), evalSteps)
                    | SequenceSuccess (eff, cont) ->
                        this.LowLevelRun eff prevAction evalSteps (SuccHandler cont :: stack)
                    | SequenceError (eff, cont) ->
                        this.LowLevelRun eff prevAction evalSteps (ErrorHandler cont :: stack)
                    | Success res ->
                        handleSuccess res newEvalSteps stack
                    | Failure err ->
                        handleError err newEvalSteps stack

            override _.Run<'R, 'E> (eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = Fiber<'R, 'E>()
                workItemQueue.Add <| WorkItem.Create (eff.Upcast()) [] (fiber.ToInternal()) Evaluated
                fiber

            member private this.CreateBlockingWorkers() : BlockingWorker list =
                let createBlockingWorkers start final =
                    List.map (fun _ ->
                    #if DETECT_DEADLOCK
                    BlockingWorker(workItemQueue, deadlockDetector, blockingEventQueue)) [start..final]
                    #else
                    BlockingWorker(workItemQueue, blockingEventQueue)) [start..final]
                    #endif
                let _, blockingWorkerCount, _ = this.GetConfiguration()
                createBlockingWorkers 0 (blockingWorkerCount - 1)

            member private this.CreateEvalWorkers blockingWorker : EvalWorker list =
                let createEvalWorkers blockingWorker evalSteps start final =
                    List.map (fun _ ->
                    #if DETECT_DEADLOCK
                    EvalWorker(this, workItemQueue, blockingWorker, deadlockDetector, evalSteps)
                    #else
                    EvalWorker(this, workItemQueue, blockingWorker, evalSteps)
                    #endif
                    ) [start..final]
                let evalWorkerCount, _, evalStepCount = this.GetConfiguration()
                createEvalWorkers blockingWorker evalStepCount 0 (evalWorkerCount - 1)

            member _.GetConfiguration() =
                let evalWorkerCount =
                    if evalWorkerCount <= 0 then System.Environment.ProcessorCount - 1
                    else evalWorkerCount
                let blockingWorkerCount =
                    if blockingWorkerCount <= 0 then 1
                    else blockingWorkerCount
                let evalStepCount =
                    if evalStepCount <= 0 then 15
                    else evalStepCount
                (evalWorkerCount, blockingWorkerCount, evalStepCount)