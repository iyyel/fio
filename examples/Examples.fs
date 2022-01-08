open FSharp.FIO

[<EntryPoint>]
let main argv =
    printfn "%s" "Examples program starting"

    let p: FIO.Effect<int> =
        FIO.receive (fun x -> FIO.receive (fun y -> FIO.send (x + y)))

    FIO.socketSend "127.0.0.1" 8888 "test"
    FIO.socketReceive

    printfn "%s" "Examples program finished"
    0