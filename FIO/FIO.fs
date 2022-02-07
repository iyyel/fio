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

type FIOVisitor =
    abstract VisitInput<'Msg, 'Success> : Input<'Msg, 'Success> -> 'Success
    abstract VisitOutput<'Msg, 'Success> : Output<'Msg, 'Success> -> 'Success
    abstract VisitConcurrent<'Async, 'Success> : Concurrent<'Async, 'Success> -> 'Success
    abstract VisitAwait<'Async, 'Success> : Await<'Async, 'Success> -> 'Success
    abstract VisitSucceed<'Success> : Succeed<'Success> -> 'Success
and [<AbstractClass>] FIO<'Success>() =
    abstract Visit<'Success> : FIOVisitor -> 'Success
and Input<'Msg, 'Success>(chan : Channel<'Msg>, cont : 'Msg -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal _.Chan = chan
    member internal _.Cont = cont
    override this.Visit<'Success>(visitor) =
        visitor.VisitInput<'Msg, 'Success>(this)
and Output<'Msg, 'Success>(value : 'Msg, chan : Channel<'Msg>, cont : unit -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal _.Value = value
    member internal _.Chan = chan
    member internal _.Cont = cont
    override this.Visit<'Success>(visitor) =
        visitor.VisitOutput<'Msg, 'Success>(this)
and Concurrent<'Task, 'Success>(eff : FIO<'Task>, cont : Task<'Task> -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal _.Eff = eff
    member internal _.Cont = cont
    override this.Visit<'Success>(visitor) =
        visitor.VisitConcurrent<'Task, 'Success>(this)
and Await<'Task, 'Success>(task : Task<'Task>, cont : 'Task -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal _.Task = task
    member internal _.Cont = cont
    override this.Visit<'Success>(visitor) =
        visitor.VisitAwait<'Task, 'Success>(this)
and Succeed<'Success>(value : 'Success) =
    inherit FIO<'Success>()
    member internal _.Value = value
    override this.Visit<'Success>(visitor) =
        visitor.VisitSucceed<'Success>(this)

let Send<'Msg, 'Success>(value : 'Msg, chan : Channel<'Msg>, cont : (unit -> FIO<'Success>)) : Output<'Msg, 'Success> = Output(value, chan, cont)
let Receive<'Msg, 'Success>(chan : Channel<'Msg>, cont : ('Msg -> FIO<'Success>)) : Input<'Msg, 'Success> = Input(chan, cont)
let Parallel<'SuccessA, 'SuccessB, 'SuccessC>(effA : FIO<'SuccessA>, effB : FIO<'SuccessB>, cont : ('SuccessA * 'SuccessB -> FIO<'SuccessC>)) : Concurrent<'SuccessA, 'SuccessC>=
    Concurrent(effA, fun taskA ->
        Concurrent(effB, fun taskB ->
            Await(taskA, fun succA ->
                Await(taskB, fun succB ->
                    cont (succA, succB)))))
let End() : Succeed<unit> = Succeed ()

module Runtime =

    let rec NaiveRun<'Success> (eff : FIO<'Success>) : 'Success =
        eff.Visit({ 
            new FIOVisitor with
                member _.VisitInput<'Msg, 'Success>(input : Input<'Msg, 'Success>) =
                    let value = input.Chan.Receive
                    NaiveRun <| input.Cont value
                member _.VisitOutput<'Msg, 'Success>(output : Output<'Msg, 'Success>) =
                    output.Chan.Send output.Value
                    NaiveRun <| output.Cont ()
                member _.VisitConcurrent(con) =
                    let task = Task.Factory.StartNew(fun () -> NaiveRun con.Eff)
                    NaiveRun <| con.Cont task
                member _.VisitAwait(await) =
                    NaiveRun <| await.Cont await.Task.Result
                member _.VisitSucceed<'Success>(succ : Succeed<'Success>) =
                    succ.Value
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