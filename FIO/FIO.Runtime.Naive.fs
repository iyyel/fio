module FIO.Runtime.Naive

open FIO.Core
open FIO.Runtime.Core

type Runtime() =
    inherit Runner()

    member internal this.LowLevelRun eff 
        (stack : List<StackFrame>) 
        : Result<obj, obj> =
            
        let rec handleSuccess res stack =
            match stack with
            | [] -> Ok res
            | s::ss -> 
                match s with
                | SuccHandler succCont ->
                    this.LowLevelRun (succCont res) ss
                | ErrorHandler _ ->
                    handleSuccess res ss
                  
        let rec handleError err stack =
            match stack with
            | [] -> Error err
            | s::ss ->
                match s with
                | SuccHandler _ ->
                    handleError err ss
                | ErrorHandler errCont ->
                    this.LowLevelRun (errCont err) ss

        let handleResult result stack =
            match result with
            | Ok res -> handleSuccess res stack
            | Error err -> handleError err stack

        match eff with
        | NonBlocking action ->
            handleResult (action ()) stack
        | Blocking chan ->
            let res = chan.Take()
            handleSuccess res stack
        | SendMessage (value, chan) ->
            chan.Add value
            handleSuccess value stack
        | Concurrent (eff, fiber, ifiber) ->
            async { ifiber.Complete <| this.LowLevelRun eff [] }
            |> Async.StartAsTask
            |> ignore
            handleSuccess fiber stack
        | AwaitFiber ifiber ->
            handleResult (ifiber.Await()) stack
        | SequenceSuccess (eff, cont) ->
            this.LowLevelRun eff (SuccHandler cont :: stack)
        | SequenceError (eff, cont) ->
            this.LowLevelRun eff (ErrorHandler cont :: stack)
        | Success res ->
            handleSuccess res stack
        | Failure err ->
            handleError err stack

    override this.Run<'R, 'E> (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
        let fiber = new Fiber<'R, 'E>()
        async { fiber.ToInternal().Complete <| this.LowLevelRun (eff.Upcast()) [] }
        |> Async.StartAsTask
        |> ignore
        fiber