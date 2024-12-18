(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

open System
open System.Net.Sockets

#load "..\FIO\FIO.Core.DSL.fs"
open FIO.Core.DSL

#load "..\FIO\FIO.Core.CE.fs"
open FIO.Core.CE

#load "..\FIO\FIO.Runtime.Core.fs"
open FIO.Runtime

#load "..\FIO\FIO.Runtime.Advanced.fs"
open FIO.Runtime.Advanced

#load "..\FIO\FIOApp.fs"
open FIO.App

#r "nuget: FSharp.SystemTextJson, 1.3.13"
#load "..\FIO\FIO.Lib.Network.fs"
open FIO.Lib.Network.Socket

type Message =
    | Connection of Username: String
    | Broadcast of Username: string * Content: string
    | PrivateMessage of SenderName: string * ReceiverName: string * Content: string

type ClientApp(userName: string, serverIp: string, port: int) =
    inherit FIOApp<unit, exn>()

    let runClient (username: string) (serverIp: string) (port: int) =
        let sendMessages userName (channel: SocketChannel<Message>) =
            fio {
                while true do
                    do! !+ printf($"%s{userName}: ")
                    let content = Console.ReadLine()
                    do! channel.Send(Broadcast(userName, content))
            }

        let receiveMessages (channel: SocketChannel<Message>) =
            fio {
                while true do
                    let! message = channel.Receive()
                    match message with
                    | Connection userName -> 
                        do! !+ printfn($"%s{userName} has joined.")
                    | Broadcast(userName, message) ->
                        do! !+ printfn($"%s{userName} says: %s{message}")
                    | PrivateMessage(sender, receiver, message) ->
                        do! !+ printfn($"%s{sender} says to %s{receiver}: %s{message}")
            }

        fio {
            let! clientSocket = !+ (new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            do! !+ clientSocket.Connect(serverIp, port)
            let! channel = !+ SocketChannel<Message>(clientSocket)
            do! !+ printfn($"Client: %s{username} has connected to server on %s{serverIp}:%d{port}")
            do! channel.Send(Connection username)
            return! sendMessages username channel <!> receiveMessages channel
        }

    override this.effect = runClient userName serverIp port

printf "Enter a username: "
let username = Console.ReadLine()
FIOApp.Run(ClientApp(username, "127.0.0.1", 5000).effect)