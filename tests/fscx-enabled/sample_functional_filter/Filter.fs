﻿//////////////////////////////////////////////////////////////////////////////
// 
// fscx - Expandable F# compiler project (testing filter code)
//   Copyright (c) 2016: Kouji Matsui (@kekyo2), bleis-tift (@bleis-tift) all rights reserved.
//
// WARNING:
//  This filter code include only useful testing, and NOT CC0 license.
//  May be replace sample filter project.
//
//////////////////////////////////////////////////////////////////////////////

module Filter

open System.Reflection
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.Ast.Visitor
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.SourceCodeServices

let zeroRange = range.Zero.MakeSynthetic()

module MethodInfo =
  open Microsoft.FSharp.Quotations
  open Microsoft.FSharp.Quotations.Patterns

  let extract (expr: Expr) =
    match expr with
    | Call(_, mi, _) -> mi
    | _ -> failwith "oops!"

  let toIdent (mi: MethodInfo) =
    assert mi.IsStatic
    // TODO : +とかジェネリックも考慮する
    let typFullName = mi.DeclaringType.FullName
    let elems = typFullName.Split('.') |> Array.toList
    let name = mi.Name
    let ids =
      List.concat [ elems; [name] ]
      |> List.map (fun x -> Ident(x, zeroRange))
    SynExpr.LongIdent(false, LongIdentWithDots(ids, List.replicate elems.Length zeroRange), None, zeroRange)

let genStringLit lit = SynExpr.Const(SynConst.String(lit, zeroRange), zeroRange)

let genIdent name = SynExpr.Ident(Ident(name, zeroRange))

let genParen x = SynExpr.Paren(x, zeroRange, None, zeroRange)

let genAppFun (name, arg) =
  SynExpr.App(ExprAtomicFlag.Atomic, false, genIdent name, arg, zeroRange)

let genOpChain (op, operands) =
  operands
  |> List.reduce (fun acc operand ->
       let func =
         SynExpr.App(ExprAtomicFlag.NonAtomic, true, genIdent op, acc, zeroRange)
       SynExpr.App(ExprAtomicFlag.NonAtomic, false, func, operand, zeroRange)
  )

let genCallStaticMethod (mi: MethodInfo, args: SynExpr list) =
  let argExpr =
    match args with
    | [] -> SynExpr.Const(SynConst.Unit, zeroRange)
    | [x] -> x
    | xs -> SynExpr.Tuple(xs, (List.replicate (xs.Length - 1) zeroRange), zeroRange)
  SynExpr.App(
    ExprAtomicFlag.Atomic,
    false,
    MethodInfo.toIdent mi,
    genParen argExpr,
    zeroRange)

let genLetExpr (name, value, expr) =
  let binding =
    SynBinding.Binding(
      None,
      SynBindingKind.NormalBinding,
      false,
      false,
      [],
      PreXmlDocEmpty,
      SynValData(None, SynValInfo([], SynArgInfo([], false, None)), None),
      SynPat.Named(SynPat.Wild(zeroRange), Ident(name, zeroRange), false, None, zeroRange),
      None,
      value,
      zeroRange,
      NoSequencePointAtInvisibleBinding)
  SynExpr.LetOrUse(false, false, [binding], expr, zeroRange)

let genTryExpr (tryExpr, clauses, range) =
  SynExpr.TryWith(
    tryExpr,
    zeroRange,
    clauses,
    zeroRange,
    zeroRange,
    SequencePointAtTry range,  // Make break point data (pdb)
    NoSequencePointAtWith)

let genClause (identName, expr) =
  SynMatchClause.Clause(
    SynPat.Named(
      SynPat.Wild(zeroRange),
      Ident(identName, zeroRange),
      false,
      None,
      zeroRange),
    None,
    expr,
    zeroRange,
    SuppressSequencePointAtTarget)

let genReraise () =
  SynExpr.App(
    ExprAtomicFlag.Atomic,
    false,
    genIdent "reraise",
    SynExpr.Paren(SynExpr.Const(SynConst.Unit, zeroRange), zeroRange, None, zeroRange),
    zeroRange)

let (+>) a b =
  SynExpr.Sequential(SuppressSequencePointOnStmtOfSequential, true, a, b, zeroRange)

let writeLineM = MethodInfo.extract <@ System.Console.WriteLine() @>

let logStartCallMethod name (ident: string) =
  genCallStaticMethod (writeLineM, [genStringLit ("calling " + name + ". args: {0}"); genIdent ident])

let logFinishCallMethod name (ident: string) =
  genCallStaticMethod (writeLineM, [genStringLit ("called " + name + ". args: {0}"); genIdent ident])

let logExn name (ident: string) =
  genCallStaticMethod (writeLineM, [genStringLit ("raised exn from " + name + ". exn: {0}"); genIdent ident])

let rec addSep sep = function
| [] -> []
| [x] -> [x]
| x::xs -> x::sep::(addSep sep xs)

let isIdent = function
| SynExpr.Ident _
| SynExpr.LongIdent _ -> true
| _ -> false

////////////////////////////////////////////////

type InsertLoggingVisitor() =
  inherit DeclareAstDelegatableVisitor<FSharpCheckFileResults>()

  //////////////////////////////////
  // Setup
  //  override __.VisitExpr_Quote(context, operator, isRaw, quoteSynExpr, isFromQueryExpression, range) =

  override __.SetupVisitor visitor =

    // SynExpr.Quote
    visitor.VisitExpr_Quote <- fun (visitor, context, operator, isRaw, quoteSynExpr, isFromQueryExpression, range) ->
      // DEBUG
      printfn "%A" operator
      None

    // SynExpr.App
    visitor.VisitExpr_App <- fun (visitor, context, exprAtomicFlag, isInfix, funcExpr, argExpr, range) ->
      match funcExpr with
      | SynExpr.Ident _
      | SynExpr.LongIdent _ -> 
        let results =
          match funcExpr with
          | SynExpr.Ident ident -> [ident.idText], ident.idRange
          | SynExpr.LongIdent (_, longIdent, _, range) ->
              let elems = longIdent.Lid |> List.map (fun i -> i.idText)
              elems, range
          | _ -> failwith "Unknown"
        let funcNameElems, funcIdentRange = results
        let opt =
          context.GetSymbolUseAtLocation(
            funcIdentRange.Start.Line,
            funcIdentRange.End.Column,
            "",
            funcNameElems)
          |> Async.RunSynchronously

        // TODO : asmが対象外だったら変換せずにorigを返す
        let asm =
          match opt with
          | Some symbolUse ->
              let symbol = symbolUse.Symbol
              let asm = symbol.Assembly
              asm.SimpleName
          | None -> "unknown"

        let funcName = (funcNameElems |> String.concat ".") + (sprintf " [%d行,%d列目]" funcIdentRange.Start.Line funcIdentRange.Start.Column)

        // from
        //   f(expr1, expr2, ..., exprN)
        // to
        //   try
        //     let arg1 = expr1
        //     let arg2 = expr2
        //     ...
        //     let argN = exprN
        //     let args = string arg1 + ", " + string arg2 + ", " + ... + string argN
        //     log1 ("f(" + args + ")")
        //     let res = f(arg1, arg2, ..., argN)
        //     log2 ("f(" + args + ")")
        //     res
        //   with
        //   | e ->
        //       log3 ("f", e)
        //       reraise ()

        match argExpr with
        // f () の考慮   => Const(Unit)
        // f (()) の考慮 => Paren(Const(Unit))
        | SynExpr.Const(SynConst.Unit, x)
        | SynExpr.Paren(SynExpr.Const(SynConst.Unit, x), _, _, _) ->
          let tryExpr =
            genLetExpr(
              "args",
              genStringLit "()",
              logStartCallMethod funcName "args"
              +> (genLetExpr (
                    "res",
                    SynExpr.App(exprAtomicFlag, isInfix, funcExpr, argExpr, zeroRange),
                    logFinishCallMethod funcName "res"
                    +> genIdent "res")))
          Some
            (genTryExpr
              (tryExpr,
               [ genClause ("e", logExn funcName "e" +> (genReraise ())) ],
               range))

        // f (x, y, ...) の考慮 => Paren(Tuple(exprs))
        | SynExpr.Paren(SynExpr.Tuple(exprs, commaRange, trange), x, y, z) ->
          let tryExpr =
            let exprs = exprs |> List.mapi (fun i x -> (i + 1, x))
            let args = exprs |> List.map (fun (n, _) -> SynExpr.Ident(Ident("arg" + string n, zeroRange)))
            let seed =
              genLetExpr(
                "args",
                genOpChain ("op_Addition", args |> List.map (fun arg -> genAppFun ("string", arg)) |> addSep (genStringLit ", ")),
                logStartCallMethod funcName "args"
                +> genLetExpr (
                      "res",
                      SynExpr.App(
                        exprAtomicFlag,
                        isInfix,
                        funcExpr,
                        SynExpr.Paren(SynExpr.Tuple(args, commaRange, trange), x, y, z),
                        zeroRange),
                      logFinishCallMethod funcName "res"
                      +> (genIdent "res")))
            let x =
              (exprs, seed)
              ||> List.foldBack (fun (n, expr) acc ->
                    let name = "arg" + string n
                    genLetExpr (name, expr, acc))
            x
          Some
            (genTryExpr
              (tryExpr,
               [ genClause ("e", logExn funcName "e" +> genReraise ()) ],
               range))

        // f (x) の考慮 => Paren(expr)
        // f x の考慮 =>  expr
        | SynExpr.Paren(expr, _, _, _)
        | expr ->
          let tryExpr =
            genLetExpr(
                "args",
                expr,
                logStartCallMethod funcName "args"
                +> (genLetExpr (
                      "res",
                      SynExpr.App(
                        exprAtomicFlag,
                        isInfix,
                        funcExpr,
                        SynExpr.Paren(expr, zeroRange, None, zeroRange),
                        zeroRange),
                      logFinishCallMethod funcName "res"
                      +> genIdent "res")))
          Some 
            (genTryExpr
              (tryExpr,
               [ genClause ("e", logExn funcName "e" +> genReraise ()) ],
               range))
      | _ ->
        None
