(************************************************************************************)
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
open FIO.Library.Network.Sockets
open FIO.Runtime.Advanced
open System.Net.WebSockets
open FIO.Library.Network.WebSockets
open System.Text

let helloWorld1 () =
    let hello: FIO<string, obj> = !+ "Hello world!"
    let fiber: Fiber<string, obj> = AdvancedRuntime().Run hello
    let result: Result<string, obj> = fiber.AwaitResult()
    printfn $"%A{result}"

let helloWorld2 () =
    let hello: FIO<obj, string> = !- "Hello world!"
    let fiber: Fiber<obj, string> = AdvancedRuntime().Run hello
    let result: Result<obj, string> = fiber.AwaitResult()
    printfn $"%A{result}"

let helloWorld3 () =
    let hello = !+ "Hello world!"
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

type SocketApp() =
    inherit FIOApp<unit, exn>()

    let server ip port = fio {
        let! listener = !+ (new TcpListener(ip, port))
        do! !+ listener.Start()
        do! !+ printfn($"Server listening on %A{ip}:%i{port}...")

        let! socket = !+ Socket<string>(listener.AcceptSocket())
        let! endpoint = socket.RemoteEndPoint()
        do! !+ printfn($"Client connected from %A{endpoint}.")

        while true do
            let! message = socket.Receive()
            do! !+ printfn($"Server received: %s{message}")

        do! socket.Close()
    }

    let client (ip: string) (port: int) = fio {
        let! internalSocket = !+ (new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        do! !+ internalSocket.Connect(ip, port)
        let! socket = !+ Socket<string>(internalSocket)

        while true do
            do! !+ printf("Enter a message: ")
            let! message = !+ Console.ReadLine()
            do! socket.Send message

        do! socket.Close()
    }

    override this.effect = fio {
        let ip = "localhost"
        let port = 5000
        return! server IPAddress.Loopback port
                <!> client ip port
    }

type WebSocketApp() =
    inherit FIOApp<unit, exn>()

    let server = 
        let handleWebSocket (webSocket: WebSocket) =
            async {
                let buffer = Array.zeroCreate 1024
                while webSocket.State = WebSocketState.Open do
                    try
                        // Receive message
                        let! result = webSocket.ReceiveAsync(ArraySegment(buffer), CancellationToken.None) |> Async.AwaitTask
                        if result.MessageType = WebSocketMessageType.Close then
                            // Close the WebSocket if a close frame is received
                            do! webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None) |> Async.AwaitTask
                        else
                            // Decode the received message
                            let message = Encoding.UTF8.GetString(buffer, 0, result.Count)
                            printfn "Received: %s" message

                            // Echo the message back to the client
                            let response = sprintf "Echo: %s" message
                            let responseBytes = Encoding.UTF8.GetBytes(response)
                            do! webSocket.SendAsync(ArraySegment(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
                    with ex ->
                        printfn "Error: %s" ex.Message
                        do! webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error", CancellationToken.None) |> Async.AwaitTask
            }
    
        fio {
            let! listener = !+ (new HttpListener())
            let! prefix = !+ $"http://localhost:8080/"
            do! !+ listener.Prefixes.Add(prefix)
            do! !+ listener.Start()
            do! !+ printfn($"WebSocket server listening at %s{prefix}")

            while true do
                try
                    do! !+ printfn("Waiting for WebSocket connection request...")
                    let! context = !+ (listener.GetContextAsync().Result)
                    do! !+ printfn($"WebSocket connection request from %A{context.Request.RemoteEndPoint}")

                    if context.Request.IsWebSocketRequest then
                        let! webSocketContext = !+ (context.AcceptWebSocketAsync(subProtocol = null).Result)
                        do! !+ printfn("WebSocket connection established")
                        return! !! !+ (Async.Start(handleWebSocket webSocketContext.WebSocket))
                    else
                        // Respond with 400 if it's not a WebSocket request
                        do! !+ (context.Response.StatusCode <- 400)
                        return! !+ context.Response.Close()
                with exn ->
                    return! !- exn
        }

    let client = fio {
        let! client = !+ (ClientWebSocket<int>(Uri("ws://localhost:8080/")))
        do! client.Connect()
        do! client.Send 42
    }

    override this.effect = fio {
        return! server <!> client
    }

helloWorld1 ()
Console.ReadLine() |> ignore

helloWorld2 ()
Console.ReadLine() |> ignore

helloWorld3 ()
Console.ReadLine() |> ignore

concurrency ()
Console.ReadLine() |> ignore

WelcomeApp().Run()
Console.ReadLine() |> ignore

EnterNumberApp().Run()
Console.ReadLine() |> ignore

GuessNumberApp().Run() // TODO: Does not work correctly.
Console.ReadLine() |> ignore

PingPongApp().Run()
Console.ReadLine() |> ignore

PingPongCEApp().Run()
Console.ReadLine() |> ignore

ErrorHandlingApp().Run()
Console.ReadLine() |> ignore

RaceServersApp().Run()
Console.ReadLine() |> ignore

HighlyConcurrentApp().Run()
Console.ReadLine() |> ignore

SocketApp().Run()
Console.ReadLine() |> ignore

WebSocketApp().Run()
Console.ReadLine() |> ignore