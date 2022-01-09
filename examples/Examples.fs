open FSharp.FIO

[<EntryPoint>]
let main argv =

    let p: FIO.Effect<string> =
        FIO.receive (fun x -> FIO.receive (fun y -> FIO.send (x + y)))

    (*
    let addr = "127.0.0.1"
    let port = 8000
    let msg = "test"

    async {
        let! result = FIO.receiveFromSocket "127.0.0.1" 8000
        printfn "Received: %s" result                               
    } |> Async.Start

    printfn "Sending message: %s" msg
    FIO.sendToSocket "127.0.0.1" 8000 msg
    *)

    FIO.naiveEval p
   
    0