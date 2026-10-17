Set-StrictMode -Version Latest

function Get-WaterfallMethodSnapshot($Assembly) {
    $snapshot = @{}
    function Walk-Types($Types) {
        foreach ($type in $Types) {
            $type
            if ($type.HasNestedTypes) { Walk-Types $type.NestedTypes }
        }
    }
    foreach ($type in (Walk-Types $Assembly.MainModule.Types)) {
        foreach ($method in $type.Methods) {
            if ($method.HasBody) {
                $snapshot[$method.FullName] = ($method.Body.Instructions | ForEach-Object { $_.ToString() }) -join "`n"
            }
        }
    }
    return ,$snapshot
}

function New-WaterfallGameDataBinary([byte[]]$Bytes) {
    $source = [IO.MemoryStream]::new($Bytes, $false)
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($source)
    $dest = [IO.MemoryStream]::new()
    try {
        if ($assembly.Name.Name -cne 'Waterfall' -or $assembly.Name.HasPublicKey) {
            throw 'Only the reviewed unsigned Waterfall assembly may be patched.'
        }
        $before = Get-WaterfallMethodSnapshot $assembly
        $changed = @()
        foreach ($target in @(
            @('Waterfall.WaterfallParticleLoader', 'LoadParticles'),
            @('Waterfall.ShaderLoader', 'LoadShaders')
        )) {
            $type = $assembly.MainModule.GetType($target[0])
            if (!$type) { throw "Missing Waterfall type: $($target[0])" }
            $methods = @($type.Methods | Where-Object Name -CEQ $target[1])
            if ($methods.Count -ne 1 -or !$methods[0].HasBody) { throw 'Unexpected Waterfall loader signature.' }
            $method = $methods[0]
            $rootCalls = @($method.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and
                $_.Operand.DeclaringType.FullName -ceq 'KSPUtil' -and
                $_.Operand.Name -ceq 'get_ApplicationRootPath'
            })
            $scans = @($method.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and
                $_.Operand.DeclaringType.FullName -ceq 'System.IO.Directory' -and
                $_.Operand.Name -ceq 'GetFiles' -and $_.Operand.Parameters.Count -eq 3
            })
            $combines = @($method.Body.Instructions | Where-Object {
                $_.Operand -is [Mono.Cecil.MethodReference] -and
                $_.Operand.DeclaringType.FullName -ceq 'System.IO.Path' -and
                $_.Operand.Name -ceq 'Combine'
            })
            if ($rootCalls.Count -ne 1 -or $scans.Count -ne 1 -or $combines.Count -ne 1 -or
                $rootCalls[0].Next.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Stelem_Ref -or
                $combines[0].Operand.Parameters[0].ParameterType.FullName -cne 'System.String[]') {
                throw 'Waterfall loader IL differs from the reviewed RC24 baseline.'
            }
            if (@($method.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and $_.Operand -ceq 'GameData' }).Count) {
                throw 'Waterfall loader already has a GameData root; do not patch twice.'
            }
            # Reuse the original mscorlib Path reference. Do not import .NET Core
            # runtime references from the build host into this KSP assembly.
            $combine = [Mono.Cecil.MethodReference]::new('Combine', $assembly.MainModule.TypeSystem.String, $combines[0].Operand.DeclaringType)
            $combine.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($assembly.MainModule.TypeSystem.String))
            $combine.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($assembly.MainModule.TypeSystem.String))
            $il = $method.Body.GetILProcessor()
            $gameData = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr, 'GameData')
            $call = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $combine)
            $il.InsertAfter($rootCalls[0], $gameData)
            $il.InsertAfter($gameData, $call)
            $changed += $method.FullName
        }
        # Fixed MVID makes the content-addressed build reproducible.
        $assembly.MainModule.Mvid = [Guid]'6f3589dc-6254-488c-b167-ecb9e50e5825'
        $assembly.Write($dest)
        $dest.Position = 0
        $check = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dest)
        try {
            $after = Get-WaterfallMethodSnapshot $check
            if ($before.Count -ne $after.Count) { throw 'Waterfall method count changed.' }
            foreach ($name in $before.Keys) {
                if ($name -cnotin $changed -and $before[$name] -cne $after[$name]) { throw "Unrelated Waterfall method changed: $name" }
            }
            foreach ($target in $changed) {
                if ($before[$target] -ceq $after[$target] -or $after[$target] -notmatch 'ldstr "GameData"') { throw 'Waterfall root patch failed validation.' }
            }
            if (($assembly.MainModule.AssemblyReferences.FullName -join "`n") -cne ($check.MainModule.AssemblyReferences.FullName -join "`n")) {
                throw 'Waterfall assembly references changed.'
            }
        } finally { $check.Dispose() }
        return ,$dest.ToArray()
    } finally { $dest.Dispose(); $assembly.Dispose(); $source.Dispose() }
}
