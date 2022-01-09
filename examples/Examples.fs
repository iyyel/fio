open FSharp.FIO

[<EntryPoint>]
let main argv =

    let p: FIO.Effect<int> =
        FIO.receive (fun x -> FIO.receive (fun y -> FIO.send (x + y)))

    let addr = "127.0.0.1"
    let port = 8000
    let msg = "test"

    let asyncReceive = async {
        let result = FIO.receiveFromSocket addr port
        printfn "Received: %s" result
    }

    Async.Start asyncReceive

    printfn "Sending message: %s" msg
    FIO.sendToSocket addr port msg 
   
    0