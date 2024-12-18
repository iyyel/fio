(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module Examples

open System

open System.Globalization
open System.Threading

open FIO.Core
open FIO.App
open FIO.Net.Socket
open FIO.Runtime.Advanced

let helloWorld1 () =
    let hello: FIO<string, obj> = succeed "Hello world!"
    let fiber: Fiber<string, obj> = AdvancedRuntime().Run hello
    let result: Result<string, obj> = fiber.Await()
    printfn $"%A{result}"

let helloWorld2 () =
    let hello = succeed "Hello world!"
    let fiber = AdvancedRuntime().Run hello
    let result = fiber.Await()
    printfn $"%A{result}"

let concurrency () =
    let concurrent = !>(succeed 42) >> fun fiber -> !?>fiber >> succeed
    let fiber = AdvancedRuntime().Run concurrent
    let result = fiber.Await()
    printfn $"%A{result}"

type WelcomeApp() =
    inherit FIOApp<unit, obj>()

    override this.effect =
        fio {
            do! succeed <| printfn "Hello! What is your name?"
            let! name = succeed <| Console.ReadLine()
            do! succeed <| printfn $"Hello, %s{name}, welcome to FIO! 🪻"
        }

type EnterNumberApp() =
    inherit FIOApp<unit, unit>()

    override this.effect =
        fio {
            do! succeed <| printf "Enter a number: "
            let! input = succeed <| Console.ReadLine()

            match Int32.TryParse(input) with
            | true, number -> do! succeed <| printfn $"You entered the number: {number}."
            | false, _ -> do! fail <| printfn "You entered an invalid number!"
        }

type GuessNumberApp() =
    inherit FIOApp<int, string>()

    override this.effect =
        fio {
            let! numberToGuess = succeed <| Random().Next(1, 100)
            let mutable guess = -1

            while guess <> numberToGuess do
                do! succeed <| printf "Guess a number: "
                let! input = succeed <| Console.ReadLine()

                match Int32.TryParse(input) with
                | true, parsedInput ->
                    guess <- parsedInput

                    if guess < numberToGuess then
                        do! succeed <| printfn "Too low! Try again."
                    elif guess > numberToGuess then
                        do! succeed <| printfn "Too high! Try again."
                    else
                        do! succeed <| printfn "Congratulations! You guessed the number!"
                | _ -> do! succeed <| printfn "Invalid input. Please enter a number."

            return guess
        }

type PingPongApp() =
    inherit FIOApp<unit, obj>()

    let channel1 = Channel<string>()
    let channel2 = Channel<string>()

    let pinger channel1 channel2 =
        "ping" *> channel1
        >> fun ping ->
            printfn $"pinger sent: %s{ping}"

            !*>channel2
            >> fun pong ->
                printfn $"pinger received: %s{pong}"
                ! ()

    let ponger channel1 channel2 =
        !*>channel1
        >> fun ping ->
            printfn $"ponger received: %s{ping}"

            "pong" *> channel2
            >> fun pong ->
                printfn $"ponger sent: %s{pong}"
                ! ()

    override this.effect = pinger channel1 channel2 <!> ponger channel1 channel2

type Error =
    | DbError of bool
    | WsError of int

type ErrorHandlingApp() =
    inherit FIOApp<string * char, obj>()

    let readFromDatabase: FIO<string, bool> =
        if Random().Next(0, 2) = 0 then !+ "data" else !- false

    let awaitWebservice: FIO<char, int> =
        if Random().Next(0, 2) = 1 then !+ 'S' else !- 404

    let databaseResult: FIO<string, Error> =
        readFromDatabase ?> fun error -> !-(DbError error)

    let webserviceResult: FIO<char, Error> =
        awaitWebservice ?> fun error -> !-(WsError error)

    override this.effect = databaseResult <^> webserviceResult ?> fun _ -> !+("default", 'D')

type RaceServersApp() =
    inherit FIOApp<string, obj>()

    let serverRegionA =
        fio {
            do! succeed <| Thread.Sleep(Random().Next(0, 101))
            return "server data (Region A)"
        }

    let serverRegionB =
        fio {
            do! succeed <| Thread.Sleep(Random().Next(0, 101))
            return "server data (Region B)"
        }

    override this.effect = serverRegionA <?> serverRegionB

// Release build required to run, will otherwise crash.
type HighlyConcurrentApp() =
    inherit FIOApp<unit, obj>()

    let rand = Random()

    let sender channel id =
        rand.Next(100, 501) *> channel
        >> fun message ->
            printfn $"Sender[%i{id}] sent: %i{message}"
            ! ()

    let rec receiver channel count (max: int) =
        if count = 0 then
            let maxFibers = max.ToString("N0", CultureInfo("en-US"))

            succeed
            <| printfn $"Successfully received a message from all %s{maxFibers} fibers"
        else
            !*>channel
            >> fun message ->
                printfn $"Receiver received: %i{message}"
                receiver channel (count - 1) max

    [<TailCall>]
    let rec create channel count acc =
        if count = 0 then
            acc
        else
            let newAcc = sender channel count <!> acc
            create channel (count - 1) newAcc

    override this.effect =
        let fiberCount = 1000000
        let channel = Channel<int>()
        let acc = sender channel fiberCount <!> receiver channel fiberCount fiberCount
        create channel (fiberCount - 1) acc

type SocketChannelApp() =
    inherit FIOApp<unit, exn>()

    let server port =
        fio {
            let! socket = SocketChannel.CreateLocalServerSocket<string>(port)
            do! succeed <| printfn $"Server listening on port %i{port}..."

            while true do
                let! message = socket.Receive()
                do! succeed <| printfn $"Server received: %s{message}"

            return socket.Close()
        }

    let client (ip: string) (port: int) =
        fio {
            let! socket = SocketChannel.CreateClientSocket<string>(ip, port)

            while true do
                do! succeed <| printf "Enter a message: "
                let! message = succeed <| Console.ReadLine()
                do! socket.Send message >> fun _ -> ! ()

            return socket.Close()
        }

    override this.effect =
        let ip = "localhost"
        let port = 5000
        server port <!> client ip port

helloWorld1 ()
Console.ReadLine() |> ignore

helloWorld2 ()
Console.ReadLine() |> ignore

concurrency ()
Console.ReadLine() |> ignore

FIOApp.Run(WelcomeApp())
Console.ReadLine() |> ignore

FIOApp.Run(EnterNumberApp())
Console.ReadLine() |> ignore

FIOApp.Run(GuessNumberApp())
Console.ReadLine() |> ignore

FIOApp.Run(PingPongApp())
Console.ReadLine() |> ignore

FIOApp.Run(ErrorHandlingApp())
Console.ReadLine() |> ignore

FIOApp.Run(RaceServersApp())
Console.ReadLine() |> ignore

FIOApp.Run(HighlyConcurrentApp())
Console.ReadLine() |> ignore

FIOApp.Run(SocketChannelApp())
Console.ReadLine() |> ignore
