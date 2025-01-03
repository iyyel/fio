﻿(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

module private FIO.Example

open System

open System.Globalization
open System.Threading
open System.Net.Sockets
open System.Net

open FIO.Core
open FIO.Library.Network.Socket
open FIO.Runtime.Advanced

let helloWorld1 () =
    let hello: FIO<string, obj> = !+ "Hello world!"
    let fiber: Fiber<string, obj> = AdvancedRuntime().Run hello
    let result: Result<string, obj> = fiber.AwaitResult()
    printfn $"%A{result}"

let helloWorld2 () =
    let hello = !+ "Hello world!"
    let fiber = AdvancedRuntime().Run hello
    let result = fiber.AwaitResult()
    printfn $"%A{result}"

let helloWorld3 () =
    let hello = !- "Hello world!"
    let fiber = AdvancedRuntime().Run hello
    let result = fiber.AwaitResult()
    printfn $"%A{result}"

let concurrency () =
    let concurrent = ! !+ 42 >>= fun fiber -> !? fiber >>= succeed
    let fiber = AdvancedRuntime().Run concurrent
    let result = fiber.AwaitResult()
    printfn $"%A{result}"

type WelcomeApp() =
    inherit FIOApp<unit, obj>()

    override this.effect = fio {
        do! !+ printfn("Hello! What is your name?")
        let! name = !+ Console.ReadLine()
        do! !+ printfn($"Hello, %s{name}, welcome to FIO! 🪻💜")
    }

type EnterNumberApp() =
    inherit FIOApp<string, string>()

    override this.effect = fio {
        do! !+ printf("Enter a number: ")
        let! input = !+ Console.ReadLine()
        match Int32.TryParse(input) with
        | true, number -> return $"You entered the number: {number}."
        | false, _ -> return! !- "You entered an invalid number!"
    }

type GuessNumberApp() =
    inherit FIOApp<int, string>()

    override this.effect = fio {
        let! numberToGuess = !+ Random().Next(1, 100)
        let mutable guess = -1

        while guess <> numberToGuess do
            do! !+ printf("Guess a number: ")
            let! input = !+ Console.ReadLine()

            match Int32.TryParse(input) with
            | true, parsedInput ->
                guess <- parsedInput
                if guess < numberToGuess then
                    do! !+ printfn("Too low! Try again.")
                elif guess > numberToGuess then
                    do! !+ printfn("Too high! Try again.")
                else
                    do! !+ printfn("Congratulations! You guessed the number!")
            | _ -> do! !+ printfn("Invalid input. Please enter a number.")

        return guess
    }

type PingPongApp() =
    inherit FIOApp<unit, obj>()

    let pinger channel1 channel2 =
        "ping" --> channel1 >>= fun ping ->
        printfn $"pinger sent: %s{ping}"
        !--> channel2 >>= fun pong ->
        printfn $"pinger received: %s{pong}"
        !+ ()

    let ponger channel1 channel2 =
        !--> channel1 >>= fun ping ->
        printfn $"ponger received: %s{ping}"
        "pong" --> channel2 >>= fun pong ->
        printfn $"ponger sent: %s{pong}"
        !+ ()

    override this.effect =
        let channel1 = Channel<string>()
        let channel2 = Channel<string>()
        pinger channel1 channel2 <!> ponger channel1 channel2

type PingPongCEApp() =
    inherit FIOApp<unit, obj>()

    let pinger (channel1: Channel<string>) (channel2: Channel<string>) = fio {
        let! ping = channel1.Send "ping"
        do! !+ printfn($"pinger sent: %s{ping}")
        let! pong = channel2.Receive()
        do! !+ printfn($"pinger received: %s{pong}")
    }

    let ponger (channel1: Channel<string>) (channel2: Channel<string>) = fio {
        let! ping = channel1.Receive()
        do! !+ printfn($"ponger received: %s{ping}")
        let! pong = channel2.Send "pong"
        do! !+ printfn($"ponger sent: %s{pong}")
    }

    override this.effect = fio {
        let channel1 = Channel<string>()
        let channel2 = Channel<string>()
        return! pinger channel1 channel2 <!> ponger channel1 channel2
    }

type Error =
    | DbError of bool
    | WsError of int

type ErrorHandlingApp() =
    inherit FIOApp<string * char, obj>()

    let readFromDatabase : FIO<string, bool> =
        if Random().Next(0, 2) = 0 then !+ "data" else !- false

    let awaitWebservice : FIO<char, int> =
        if Random().Next(0, 2) = 1 then !+ 'S' else !- 404

    let databaseResult : FIO<string, Error> =
        readFromDatabase >>? fun error -> !- (DbError error)

    let webserviceResult : FIO<char, Error> =
        awaitWebservice >>? fun error -> !- (WsError error)

    override this.effect =
        databaseResult <^> webserviceResult >>? fun _ -> !+ ("default", 'D')

type RaceServersApp() =
    inherit FIOApp<string, obj>()

    let serverRegionA = fio {
        do! !+ Thread.Sleep(Random().Next(0, 101))
        return "server data (Region A)"
    }

    let serverRegionB = fio {
        do! !+ Thread.Sleep(Random().Next(0, 101))
        return "server data (Region B)"
    }

    override this.effect = fio {
        return! serverRegionA <?> serverRegionB
    }

// Release build required to run, will otherwise crash.
type HighlyConcurrentApp() =
    inherit FIOApp<unit, obj>()

    let sender (channel: Channel<int>) id (random: Random) = fio {
        let! message = !+ random.Next(100, 501)
        do! message -!> channel
        do! !+ printfn($"Sender[%i{id}] sent: %i{message}")
    }

    let rec receiver (channel: Channel<int>) count (max: int) = fio {
        if count = 0 then
            let! maxFibers = !+ max.ToString("N0", CultureInfo("en-US"))
            do! !+ printfn($"Successfully received a message from all %s{maxFibers} fibers!")
        else
            let! message = !<-- channel
            do! !+ printfn($"Receiver received: %i{message}")
            return! receiver channel (count - 1) max
    }

    [<TailCall>]
    let rec create channel count acc random = fio {
        if count = 0 then
            return! acc
        else
            let newAcc = sender channel count random <!> acc
            return! create channel (count - 1) newAcc random
    }

    override this.effect = fio {
        let fiberCount = 1000000
        let channel = Channel<int>()
        let random = Random()
        let acc = sender channel fiberCount random
                  <!> receiver channel fiberCount fiberCount
        return! create channel (fiberCount - 1) acc random
    }

type SocketChannelApp() =
    inherit FIOApp<unit, exn>()

    let server ip port = fio {
        let! listener = !+ (new TcpListener(ip, port))
        do! !+ listener.Start()
        do! !+ printfn($"Server listening on %A{ip}:%i{port}...")

        let! socketChannel = !+ SocketChannel<string>(listener.AcceptSocket())
        let! endpoint = socketChannel.RemoteEndPoint()
        do! !+ printfn($"Client connected from %A{endpoint}.")

        while true do
            let! message = socketChannel.Receive()
            do! !+ printfn($"Server received: %s{message}")

        do! socketChannel.Close()
    }

    let client (ip: string) (port: int) = fio {
        let! socket = !+ (new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        do! !+ socket.Connect(ip, port)
        let! socketChannel = !+ SocketChannel<string>(socket)

        while true do
            do! !+ printf("Enter a message: ")
            let! message = !+ Console.ReadLine()
            do! socketChannel.Send message

        do! socketChannel.Close()
    }

    override this.effect = fio {
        let ip = "localhost"
        let port = 5000
        return! server IPAddress.Loopback port
                <!> client ip port
    }

helloWorld1 ()
Console.ReadLine() |> ignore

helloWorld2 ()
Console.ReadLine() |> ignore

helloWorld3 ()
Console.ReadLine() |> ignore

concurrency ()
Console.ReadLine() |> ignore

FIOApp.Run(WelcomeApp())
Console.ReadLine() |> ignore

FIOApp.Run(EnterNumberApp())
Console.ReadLine() |> ignore

FIOApp.Run(GuessNumberApp()) // TODO: Does not work correctly.
Console.ReadLine() |> ignore

FIOApp.Run(PingPongApp())
Console.ReadLine() |> ignore

FIOApp.Run(PingPongCEApp())
Console.ReadLine() |> ignore

FIOApp.Run(ErrorHandlingApp())
Console.ReadLine() |> ignore

FIOApp.Run(RaceServersApp())
Console.ReadLine() |> ignore

FIOApp.Run(HighlyConcurrentApp())
Console.ReadLine() |> ignore

FIOApp.Run(SocketChannelApp())
Console.ReadLine() |> ignore
