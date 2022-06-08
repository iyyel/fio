(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module Examples

open FSharp.FIO

open System
open System.Threading

let helloWorldExample1 () =
    let hello : FIO<string, obj> = succeed "Hello world!"
    let fiber : Fiber<string, obj> = Advanced.Runtime().Run hello
    let result : Result<string, obj> = fiber.Await()
    printfn $"%A{result}"

let helloWorldExample2 () =
    let hello = succeed "Hello world!"
    let fiber = Advanced.Runtime().Run hello
    let result = fiber.Await()
    printfn $"%A{result}"

let concurrencyExample () =
    let spawner = spawn (succeed 42) >> fun fiber ->
                  await fiber >> fun result ->
                  succeed result
    let fiber = Advanced.Runtime().Run spawner
    let result = fiber.Await()
    printfn $"%A{result}"

type Error = 
    | DbError of bool
    | WsError of int

let errorHandlingExample () = 
    let readFromDatabase : FIO<string, bool> =
        let rand = Random()
        if rand.Next(0, 2) = 0 then
            succeed "data"
        else
            fail false

    let awaitWebservice : FIO<char, int> =
        let rand = Random()
        if rand.Next(0, 2) = 1 then
            succeed 'S'
        else
            fail 404

    let databaseResult : FIO<string, Error> =
        attempt readFromDatabase (fun err -> fail (DbError err))

    let webserviceResult : FIO<char, Error> =
        attempt awaitWebservice (fun err -> fail (WsError err))

    let program : FIO<string * char, Error> =
        let result = zip databaseResult webserviceResult
        attempt result (fun _ -> succeed ("default", 'D'))
  
    let fiber = Naive.Runtime().Run program
    let result = fiber.Await()
    printfn $"%A{result}"

let raceServicesExample () =
    let serverRegionA =
        let rand = Random()
        fio (fun _ ->
        succeed (Thread.Sleep(rand.Next(0, 101))))
        >> fun _ ->
        succeed "server data (Region A)"
          
    let serverRegionB =
        let rand = Random()
        fio (fun _ ->
        succeed (Thread.Sleep(rand.Next(0, 101))))
        >> fun _ ->
        succeed "server data (Region B)"

    let program = race serverRegionA serverRegionB

    let fiber = Advanced.Runtime().Run program
    let result = fiber.Await()
    printfn $"%A{result}"

let pingPongMpExample () =
    let pinger chan1 chan2 =
        let ping = "ping"
        send ping chan1 >> fun _ ->
        printfn $"pinger sent: %s{ping}"
        receive chan2 >> fun pong ->
        printfn $"pinger received: %s{pong}"
        stop

    let ponger chan1 chan2 =
        receive chan1 >> fun ping ->
        printfn $"ponger received: %s{ping}"
        let pong = "pong"
        send pong chan2 >> fun _ ->
        printfn $"ponger sent: %s{pong}"
        stop

    let chan1 = Channel<string>()
    let chan2 = Channel<string>()
    let pingpong = pinger chan1 chan2 ||| ponger chan1 chan2

    let fiber = Advanced.Runtime().Run pingpong
    let result = fiber.Await()
    printfn $"%A{result}"

let highConcurrencyExample () =
    let sender chan id =
        let msg = 42
        send msg chan >> fun _ ->
        printfn $"Sender[%i{id}] sent: %i{msg}"
        stop

    let rec receiver chan count =
        if count = 0 then
            stop
        else
            receive chan >> fun msg ->
            printfn $"Receiver received: %i{msg}"
            receiver chan (count - 1)

    let rec create chan count acc =
        if count = 0 then
            acc
        else
            let newAcc = sender chan count |||* acc
            create chan (count - 1) newAcc

    let fiberCount = 100000
    let chan = Channel<int>()
    let acc = sender chan fiberCount |||* receiver chan fiberCount
    let program = create chan (fiberCount - 1) acc

    let fiber = Advanced.Runtime().Run program
    let result = fiber.Await()
    printfn $"%A{result}"

let spawnFiberExample () =
    let effect = 
        spawn (succeed 42) >> fun fiber ->
        await fiber >> succeed
    effect