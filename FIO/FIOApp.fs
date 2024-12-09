module FIO.App

open FIO.Core
open FIO.Runtime
open FIO.Runtime.Advanced

open System.Threading

let maxThreads = 32767
ThreadPool.SetMaxThreads(maxThreads, maxThreads) |> ignore
ThreadPool.SetMinThreads(maxThreads, maxThreads) |> ignore

let internal defaultRuntime = AdvancedRuntime()

[<AbstractClass>]
type FIOApp<'R, 'E>() =

    static member Run<'R, 'E> (fioApp : FIOApp<'R, 'E>) : Unit =
        fioApp.Run()

    static member Run<'R, 'E> (effect : FIO<'R, 'E>) : Unit =
        let fiber = defaultRuntime.Run effect
        printfn $"%A{fiber.Await()}"

    abstract member effect : FIO<'R, 'E>

    member this.Run() : Unit =
        let fiber = defaultRuntime.Run this.effect
        printfn $"%A{fiber.Await()}"

    member this.Run(runtime : Runtime) : Unit =
        let fiber = runtime.Run this.effect 
        printfn $"%A{fiber.Await()}"