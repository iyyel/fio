open FSharp.FIO

[<EntryPoint>]
let main argv =
    printfn "%s" "Examples program starting"

    let p: FIO.Effect<int> =
        FIO.receive (fun x -> FIO.receive (fun y -> FIO.send (x + y)))

    let msg = "test"

    let asyncReceive = async {
        let result = FIO.receiveFromSocket
        printfn "Received: %s" result
    }

    Async.Start asyncReceive

    printfn "Sending message: %s" msg
    FIO.sendToSocket msg
   
    printfn "%s" "Examples program finished"
    0