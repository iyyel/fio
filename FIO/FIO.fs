// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks
open System.Threading

type Channel<'Msg>() =
    let bc = new BlockingCollection<'Msg>()
    member this.Send value = bc.Add value
    member this.Receive = bc.Take()

type Either<'Error, 'Result> =
    | Left of 'Error
    | Right of 'Result

type FIOVisitor =
    abstract VisitInput<'Msg, 'Error, 'Result> : Input<'Msg, 'Error, 'Result> -> Either<'Error, 'Result>
    abstract VisitOutput<'Msg, 'Error, 'Result> : Output<'Msg, 'Error, 'Result> -> Either<'Error, 'Result>
    abstract VisitConcurrent<'Async, 'Error, 'Result> : Concurrent<'Async, 'Error, 'Result> -> Either<'Error, 'Result>
    abstract VisitAwait<'TaskResult, 'Error, 'Result> : Await<'TaskResult, 'Error, 'Result> -> Either<'Error, 'Result>
    abstract VisitSucceed<'Result> : Succeed<'Result> -> 'Result
    abstract VisitFail<'Error> : Fail<'Error> -> 'Error
and [<AbstractClass>] FIO<'Error, 'Result>() =
    abstract Accept<'Error, 'Result> : FIOVisitor -> Either<'Error, 'Result>
and Input<'Msg, 'Error, 'Result>(chan : Channel<'Msg>, cont : 'Msg -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Chan = chan
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitInput<'Msg, 'Error, 'Result>(this)
and Output<'Msg, 'Error, 'Result>(value : 'Msg, chan : Channel<'Msg>, cont : unit -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Value = value
    member internal _.Chan = chan
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitOutput<'Msg, 'Error, 'Result>(this)
and Concurrent<'TaskResult, 'Error, 'Result>(eff : FIO<'Error, 'TaskResult>, cont : Task<Either<'Error, 'TaskResult>> -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Eff = eff
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitConcurrent<'TaskResult, 'Error, 'Result>(this)
and Await<'Task, 'Error, 'Result>(task : Task<'Task>, cont : 'Task -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Task = task
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitAwait<'Task, 'Error, 'Result>(this)
and Succeed<'Result>(value : 'Result) =
    inherit FIO<unit, 'Result>()
    member internal _.Value = value
    override this.Accept<'Error, 'Result>(visitor) =
        Right (visitor.VisitSucceed<'Result>(this))
and Fail<'Error>(value : 'Error) =
    inherit FIO<'Error, unit>()
    member internal _.Value = value
    override this.Accept<'Error, 'Result>(visitor) =
        Left (visitor.VisitFail<'Error>(this))

let Send<'Msg, 'Error, 'Result>(value : 'Msg, chan : Channel<'Msg>, cont : unit -> FIO<'Error, 'Result>) : Output<'Msg, 'Error, 'Result> = Output(value, chan, cont)
let Receive<'Msg, 'Error, 'Result>(chan : Channel<'Msg>, cont : 'Msg -> FIO<'Error, 'Result>) : Input<'Msg, 'Error, 'Result> = Input(chan, cont)
let Parallel<'Error, 'ResultA, 'ResultB, 'ResultC>(effA : FIO<'Error, 'ResultA>, effB : FIO<'Error, 'ResultB> , cont : Either<'Error, 'ResultA> * Either<'Error, 'ResultB> -> FIO<'Error, 'ResultC>) : Concurrent<'ResultA, 'Error, 'ResultC> =
    Concurrent(effA, fun taskA ->
        Concurrent(effB, fun taskB ->
            Await(taskA, fun resA ->
                Await(taskB, fun resB ->
                    cont (resA, resB)))))
let End() : Succeed<unit> = Succeed ()

module Runtime =

    let rec NaiveRun<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Either<'Error, 'Result> =
        eff.Accept({ 
            new FIOVisitor with
                member _.VisitInput<'Msg, 'Error, 'Result>(input : Input<'Msg, 'Error, 'Result>) =
                    let value = input.Chan.Receive
                    NaiveRun <| input.Cont value
                member _.VisitOutput<'Msg, 'Error, 'Result>(output : Output<'Msg, 'Error, 'Result>) =
                    output.Chan.Send output.Value
                    NaiveRun <| output.Cont ()
                member _.VisitConcurrent<'Async, 'Error, 'Result>(con : Concurrent<'Async, 'Error, 'Result>) =
                    let task = Task.Factory.StartNew(fun () -> NaiveRun con.Eff)
                    NaiveRun <| con.Cont task
                member _.VisitAwait(await) =
                    NaiveRun <| await.Cont await.Task.Result
                member _.VisitSucceed<'Result>(res : Succeed<'Result>) =
                    res.Value
                member _.VisitFail<'Error>(fail : Fail<'Error>) =
                    fail.Value
        })

    let PrintThreadPoolInfo() =
        let maxWorkerThreads = ref 0;
        let maxCompletePortThreads = ref 0;
        let minWorkerThreads = ref 0;
        let minCompletePortThreads = ref 0;
        let avlWorkerThreads = ref 0;
        let avlIoThreads = ref 0;
        ThreadPool.GetMaxThreads(maxWorkerThreads, maxCompletePortThreads)
        ThreadPool.GetMinThreads(minWorkerThreads, minCompletePortThreads)
        ThreadPool.GetAvailableThreads(avlWorkerThreads, avlIoThreads);
        printfn $"Thread pool information: "
        printfn $"Maximum worker threads: %A{maxWorkerThreads}"
        printfn $"Maximum completion port threads %A{maxCompletePortThreads}"
        printfn $"Minimum worker threads: %A{minWorkerThreads}"
        printfn $"Minimum completion port threads %A{minCompletePortThreads}"
        printfn $"Available worker threads: %A{avlWorkerThreads}"
        printfn $"Available I/O threads: %A{avlIoThreads}"