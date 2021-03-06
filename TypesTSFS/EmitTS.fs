﻿module EmitTS

open FSharp.Compiler.SourceCodeServices
type IList<'a> = System.Collections.Generic.IList<'a>

type Style = 
    | WebSharper
    | JsonNet

let namedOrNumber name i =
    match name with
    | "" -> ("p" + string i)
    | _ -> name

let rec unabbreviate (possiblyAbbreviated:FSharpType) =
    if possiblyAbbreviated.IsAbbreviation then
        possiblyAbbreviated.AbbreviatedType
    else
        possiblyAbbreviated

let rec typeToTS (fsharpType:FSharpType) =

    if fsharpType.IsAbbreviation then
        typeToTS (unabbreviate fsharpType)
    else
        if fsharpType.IsGenericParameter then
            fsharpType.GenericParameter.DisplayName
        elif fsharpType.IsFunctionType then

            //Note: this doesn't correctly get the arguments, it's also problematic with overload name mangling

            let inputs = fsharpType.GenericArguments |> Seq.take (fsharpType.GenericArguments.Count - 1)
            let output = fsharpType.GenericArguments |> Seq.skip (fsharpType.GenericArguments.Count - 1) |> Seq.exactlyOne

            let argumentsAsString = inputs |> Seq.mapi (fun i argument -> (namedOrNumber "" i) + ": " + typeToTS argument) |> String.concat ", "
            let stringed = sprintf "(%s) => %s" argumentsAsString (typeToTS output)
            stringed
        elif fsharpType.IsTupleType then
            "Opaque.FSharpTuple" //Not currently supported
        else

            if
                (fsharpType.TypeDefinition.DisplayName = "Dictionary" ||
                 fsharpType.TypeDefinition.DisplayName = "Map") &&
                fsharpType.GenericArguments.[0].HasTypeDefinition &&
                fsharpType.GenericArguments.[0].TypeDefinition.DisplayName = "string" then
                
                sprintf "{ [key: string]: %s }" (typeToTS fsharpType.GenericArguments.[1])
            else

                let stem =
                    match fsharpType.TypeDefinition.DisplayName with
                    | "int" //caution with ints and JavaScript number
                    | "Int32"  
                    | "uint32"
                    | "UInt32"
                    | "float"
                    | "Double" -> "number"
                    | "bool"
                    | "Boolean" -> "boolean"
                    | "list" 
                    | "List" 
                    | "set"
                    | "Set"
                    | "IEnumerable" //TODO: check this really goes to array
                    | "[]" -> "Array"
                    | "char"
                    | "Char"
                    | "string"
                    | "String" -> "string"
                    | "Unit" -> "void"
                    | "Dictionary" -> "Opaque.Dictionary"
                    | "Map" -> "Opaque.FSharpMap"
                    | x -> fsharpType.TypeDefinition.AccessPath + "." + x

                if stem = "number" then
                    stem
                elif stem = "Microsoft.FSharp.Core.Option" then
                    typeToTS fsharpType.GenericArguments.[0] + " | null"
                elif fsharpType.GenericArguments.Count > 0 then
                    stem + "<" + (fsharpType.GenericArguments |> Seq.map typeToTS |> String.concat ", ") + ">"
                else
                    stem

let onlyExportedEntities (possibleEntities: FSharpEntity[]) =
    possibleEntities
    |> Array.filter(fun entity ->
        match entity.DisplayName with
        | "List"
        | "Option" -> false
        | _ -> true)

let parameterAndTypeToTS parameterName (fsharpType:FSharpType) =

    //We have to check it's not some of these or will throw on accessing TypeDefinition
    //TODO: handle abbreviations
    let isOption =
        //fsharpType.IsAbbreviation &&
        not fsharpType.IsGenericParameter &&
        not fsharpType.IsFunctionType &&
        not fsharpType.IsTupleType &&
        (fsharpType.TypeDefinition.DisplayName = "option" || fsharpType.TypeDefinition.DisplayName = "Option") //Bit much to assume this is the right "option"

    if isOption then
        sprintf "%s?: %s" parameterName (typeToTS fsharpType.GenericArguments.[0])
    else
        sprintf "%s: %s" parameterName (typeToTS fsharpType)

let genericParamtersToDisplay (genericParameters:System.Collections.Generic.IList<FSharpGenericParameter>) =
    genericParameters
    |> Seq.map(fun genericParameter -> genericParameter.DisplayName)


let inline displayName (x:^t) =
    (^t: (static member DisplayName: ^t -> string) (x))


let nameAndParametersToString name parameters =
    if Seq.isEmpty parameters then
        name
    else
        name + "<" + (String.concat ", " (genericParamtersToDisplay parameters)) + ">"

let argumentsToString (arguments:IList<IList<FSharpParameter>>) =
    arguments
    |> Seq.mapi(fun i parameterGroup -> (namedOrNumber parameterGroup.[0].DisplayName i) + ": " + (typeToTS parameterGroup.[0].Type))
    |> String.concat ", "

let entityNameToString (entity:FSharpEntity) = nameAndParametersToString entity.DisplayName entity.GenericParameters
let memberNameToString (entity:FSharpMemberOrFunctionOrValue) = 
    let rest = nameAndParametersToString entity.DisplayName entity.GenericParameters
    //Function types not reliably converted yet
    if entity.IsMember && entity.FullType.IsFunctionType then
        "//" + rest
    else
        rest
let membersToString members =
    members
    |> Seq.map (fun member_ -> sprintf "            %s : (%s) => %s;" (memberNameToString member_) (argumentsToString (Seq.skip 1 member_.CurriedParameterGroups |> Seq.toArray)) (typeToTS member_.ReturnParameter.Type))
    |> String.concat "\r\n"

let entityToString (style:Style) (namespacename:string) (nested:FSharpEntity[]) =

    //Restrict to only entities that should be exported

    let restrictedEntities = onlyExportedEntities nested
    
    //Records

    let records = restrictedEntities |> Seq.filter (fun entity -> entity.IsFSharpRecord)

    let recordFieldsToString record = String.concat "\r\n" (record |> Seq.map (fun (recordField:FSharpField) -> sprintf "            %s;" (parameterAndTypeToTS recordField.DisplayName recordField.FieldType)))
    let recordTypeGenericParameters (entity:FSharpEntity) =
            
        if entity.GenericParameters.Count = 0 then
            ""
        else
            "<" + (entity.GenericParameters |> Seq.map (fun param -> param.Name) |> String.concat ", ") + ">"
                
    let recordsAsStrings =
        records
        |> Seq.map (fun record -> sprintf "        export interface %s%s {\r\n%s\r\n        }" record.DisplayName (recordTypeGenericParameters record) (recordFieldsToString record.FSharpFields))


    //Unions

    let unions = restrictedEntities |> Seq.filter (fun entity -> entity.IsFSharpUnion)

    let isNumber (type_:FSharpType) =

        let reallyType = if type_.IsAbbreviation then type_.AbbreviatedType else type_

        reallyType.HasTypeDefinition &&
        not reallyType.IsAbbreviation &&
        not reallyType.IsGenericParameter &&
        not reallyType.IsFunctionType &&
        not reallyType.IsTupleType &&
        (reallyType.TypeDefinition.DisplayName = "Double" || //Bit much to assume this is the right "option"
         reallyType.TypeDefinition.DisplayName = "int32")

    let (|JustName|Type|Types|) (case:FSharpUnionCase) =

        match case.UnionCaseFields.Count with
        | 0 -> JustName case.DisplayName
        | 1 -> Type case.UnionCaseFields.[0].FieldType
        | _ -> Types case.UnionCaseFields

    let (|AllJustNames|NamesAndNumbers|ComplexTypes|) (cases:IList<FSharpUnionCase>) =
        if cases |> Seq.exists (fun case -> case.UnionCaseFields.Count > 0) then
            if cases |> Seq.collect (fun case -> case.UnionCaseFields) |> Seq.exists (fun field -> not (isNumber field.FieldType)) then
                ComplexTypes cases
            else
                NamesAndNumbers cases
        else
            AllJustNames cases

    let caseAsStringWebSharper (allCases) (case:FSharpUnionCase) =
        let optional = if (Seq.length allCases) = 1 then "" else "?" 
        match case with
        | JustName name -> sprintf "            %s%s: string;" name optional
        | Type singleType -> sprintf "            %s%s: %s;" case.DisplayName optional (typeToTS singleType)
        | Types types ->

            let stuff =
                let uniqueTypes = types |> Seq.distinctBy(fun type1 -> type1.FieldType.TypeDefinition.DisplayName)
                    
                let typesAsString = 
                    uniqueTypes
                    |> Seq.map(fun field -> field.FieldType)
                    |> Seq.map typeToTS
                    |> String.concat " | "

                if Seq.length uniqueTypes = 1 then
                    typesAsString
                else
                    "(" + typesAsString + ")"
            
            sprintf "            %s%s: %s[];" case.DisplayName optional stuff

    let casesAsStringJsonNet (allCases:IList<FSharpUnionCase>) =
        allCases
        |> Seq.collect(fun case -> case.UnionCaseFields |> Seq.map (fun field -> field.FieldType |> typeToTS))
        |> String.concat " | "

    let caseNamesJsonNet (allCases:IList<FSharpUnionCase>) =
        allCases
        |> Seq.map (fun case -> "\"" + case.DisplayName + "\"")
        |> String.concat " | "

    let nameOrNumberToString (case:FSharpUnionCase) =
        if case.UnionCaseFields.Count = 1 && isNumber case.UnionCaseFields.[0].FieldType then
            sprintf "{ %s: number }" case.DisplayName
        else
            "\"" + case.DisplayName + "\""
                
    let casesAsString (cases:IList<FSharpUnionCase>) =
        match style with
        | WebSharper -> cases |> Seq.map (cases |> caseAsStringWebSharper) |> String.concat "\r\n"
        //| JsonNet -> "            Case: String;" + "\r\n" + "            Fields: Array<any>"
        | JsonNet -> sprintf "            Case: %s;\r\n            Fields: Array<%s>;" (caseNamesJsonNet cases) (casesAsStringJsonNet cases)
    
    let namesAndNumbersCasesAsString = Seq.map nameOrNumberToString >> String.concat " | "

    let namesAndNumbersArrayAsString = Seq.map nameOrNumberToString >> String.concat ", "

    let unionsAsString =
        unions
        |> Seq.map (fun union ->
            match style, union.UnionCases with
            | Style.JsonNet, ComplexTypes cases
            | Style.WebSharper, ComplexTypes cases -> sprintf "        export interface %s {\r\n%s\r\n        }" (entityNameToString union) (casesAsString cases)
            | Style.JsonNet, cases -> sprintf "        export interface %s { Case: %s }" union.DisplayName (caseNamesJsonNet cases)
            | Style.WebSharper, NamesAndNumbers cases ->
                sprintf "        export type %s = %s" union.DisplayName (namesAndNumbersCasesAsString cases)
            | Style.WebSharper, AllJustNames cases ->
                sprintf "        export type %s = %s\r\n        export const %sSelect = [%s]" union.DisplayName (namesAndNumbersCasesAsString cases) union.DisplayName (namesAndNumbersArrayAsString cases))

    //Classes

    let classes = restrictedEntities |> Seq.filter (fun entity -> entity.IsFSharp && (entity.IsClass || entity.IsInterface))

    let classesAsString =
        classes |> Seq.map (fun class_ ->
            //TODO: fields
            let members =
                class_.MembersFunctionsAndValues
                |> Seq.filter (fun entity -> entity.IsMember && (not entity.IsConstructor) && (not entity.IsProperty)) //TODO: deal with constructors
            if members |> Seq.isEmpty then
                sprintf "        export interface %s { }" (entityNameToString class_)
            else
                sprintf "        export interface %s {\r\n%s\r\n        }" (entityNameToString class_) (membersToString members))


    //Opaques -- hidden implementations

    let opaques = restrictedEntities |> Seq.filter (fun entity -> entity.IsFSharp && entity.IsOpaque)

    //Should these be aliases for any instead?
    let opaquesAsString = opaques |> Seq.map (fun class_ -> sprintf "        export interface %s { }" (entityNameToString class_))

    //Concat them

    let allAsStrings = [classesAsString; opaquesAsString; recordsAsStrings; unionsAsString] |> Seq.concat

    let contents = String.concat "\r\n" allAsStrings

    let withModuleWrapping = sprintf "    export namespace %s {\r\n%s\r\n    }" namespacename contents
    withModuleWrapping



let functionAsStrings (functions:seq<FSharpMemberOrFunctionOrValue>) =
    functions
    |> Seq.map(fun value -> sprintf "    export interface %s {\r\n        (%s): %s\r\n    }\r\n" (memberNameToString value) (argumentsToString value.CurriedParameterGroups) (typeToTS value.ReturnParameter.Type))
    |> String.concat "\r\n"