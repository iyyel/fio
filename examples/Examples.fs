open FSharp.FIO

let p: FIO.Effect<string> =
    FIO.send("test 1", fun () ->
        FIO.send("test 2", fun () -> 
            FIO.receive(fun x -> 
                FIO.receive(fun y ->
                    FIO.send(x + " " + y, fun () ->
                        FIO.receive(fun y -> FIO.Unit))))))

let p1: FIO.Effect<string> =
    FIO.receive(fun x -> 
        FIO.receive (fun y -> 
            FIO.send(x + y, fun () -> FIO.Unit)))

let p2: FIO.Effect<string> = FIO.Parallel(
    FIO.send("message 1", fun () -> FIO.receive (fun x -> FIO.Unit)),
    FIO.send("message 2", fun () -> FIO.receive (fun x -> FIO.Unit)))

[<EntryPoint>]
let main argv =
    
    FIO.naiveEval p
    FIO.naiveEval p1
    FIO.naiveEval p2

    0