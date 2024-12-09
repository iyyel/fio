module FIOAppExamples

open System

open FIO.Core
open FIO.App

type MyFIOApp() =
    inherit FIOApp<Unit, obj>()

    override this.effect =
        fio {
            do! !+ (printfn "%s" "Hello! What is your name?")
            let! name = !+ Console.ReadLine()
            do! !+ (printfn $"Hello, %s{name}, welcome to FIO!")
        }

type MyCustomApp() =
    inherit FIOApp<string, string>()

    override this.effect =
        fio {
            do! !+ (printfn "%s" "Enter a number: ")
            let! input = !+ Console.ReadLine()
            match Int32.TryParse(input) with
            | true, number -> return  $"You entered: {number}"
            | false, _ -> return! !- "Invalid number entered!"
        }

type MyPingerApp(channel1: Channel<string>, channel2: Channel<string>) =
    inherit FIOApp<unit, obj>()

    override this.effect =
        "ping" *> channel1 >> fun ping ->
        printfn $"pinger sent: %s{ping}"
        !*> channel2 >> fun pong ->
        printfn $"pinger received: %s{pong}"
        ! ()

type MyPongerApp(channel1: Channel<string>, channel2: Channel<string>) =
    inherit FIOApp<unit, obj>()

    override this.effect =
        !*> channel1 >> fun ping ->
        printfn $"ponger received: %s{ping}"
        "pong" *> channel2 >> fun pong ->
        printfn $"ponger sent: %s{pong}"
        ! ()

type MyPingPongApp() =
    inherit FIOApp<unit, obj>()

    override this.effect =
        let channel1 = Channel<string>()
        let channel2 = Channel<string>()
        MyPingerApp(channel1, channel2).effect <!> MyPongerApp(channel1, channel2).effect

MyFIOApp().Run()
MyCustomApp().Run()
MyPingPongApp().Run()
FIOApp.Run(MyPingPongApp())