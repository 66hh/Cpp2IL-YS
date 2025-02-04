using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.CorePlugin;

public class X86InstructionSet : Cpp2IlInstructionSet
{
    
    public override Memory<byte> GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator) => X86Utils.GetRawManagedOrCaCacheGenMethodBody(context.UnderlyingPointer, isAttributeGenerator);

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new X86KeyFunctionAddresses();

    public override string PrintAssembly(MethodAnalysisContext context)
    {
        var insns = X86Utils.Disassemble(X86Utils.GetRawManagedOrCaCacheGenMethodBody(context.UnderlyingPointer, false), context.UnderlyingPointer);

        return string.Join("\n", insns);
    }
    
    public override List<InstructionSetIndependentInstruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        var insns = X86Utils.Disassemble(context.RawBytes, context.UnderlyingPointer);
        
        

        var builder = new IsilBuilder();
        
        foreach (var instruction in insns)
        {
            ConvertInstructionStatement(instruction, builder, context);
        }

        builder.FixJumps();
        
        return builder.BackingStatementList;
    }


    private void ConvertInstructionStatement(Instruction instruction, IsilBuilder builder, MethodAnalysisContext context)
    {
        switch (instruction.Mnemonic)
        {
            case Mnemonic.Mov:
                builder.Move(instruction.IP, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Lea:
                builder.LoadAddress(instruction.IP, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Xor:
                if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register && instruction.Op0Register == instruction.Op1Register)
                    builder.Move(instruction.IP, ConvertOperand(instruction, 0), InstructionSetIndependentOperand.MakeImmediate(0));
                else
                    builder.Xor(instruction.IP, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Ret:
                if(context.IsVoid)
                    builder.Return(instruction.IP);
                else
                    builder.Return(instruction.IP, InstructionSetIndependentOperand.MakeRegister("rax")); //TODO Support xmm0
                break;
            case Mnemonic.Push:
            {
                //var operandSize = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize();
                //builder.ShiftStack(instruction.IP, -operandSize);
                builder.Push(instruction.IP, InstructionSetIndependentOperand.MakeRegister("rsp"), ConvertOperand(instruction, 0));
                break;
            }
            case Mnemonic.Pop:
            {
                //var operandSize = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize();
                builder.Pop(instruction.IP, InstructionSetIndependentOperand.MakeRegister("rsp"),ConvertOperand(instruction, 0));
                //builder.ShiftStack(instruction.IP, operandSize);
                break;
            }
            case Mnemonic.Sub:
            case Mnemonic.Add:
                var isSubtract = instruction.Mnemonic == Mnemonic.Sub;
                
                //Special case - stack shift
                if (instruction.Op0Register == Register.RSP && instruction.Op1Kind.IsImmediate())
                {
                    var amount = (int) instruction.GetImmediate(1);
                    builder.ShiftStack(instruction.IP, isSubtract ? -amount : amount);
                    break;
                }

                var left = ConvertOperand(instruction, 0);
                var right = ConvertOperand(instruction, 1);
                if(isSubtract)
                    builder.Subtract(instruction.IP, left, right);
                else
                    builder.Add(instruction.IP, left, right);
                
                break;
            case Mnemonic.Call:
                       //We don't try and resolve which method is being called, but we do need to know how many parameters it has
                //I would hope that all of these methods have the same number of arguments, else how can they be inlined?
                var target = instruction.NearBranchTarget;
                if (context.AppContext.MethodsByAddress.ContainsKey(target))
                {
                    var possibleMethods = context.AppContext.MethodsByAddress[target];
                    var parameterCounts = possibleMethods.Select(p =>
                    {
                        var ret = p.Parameters.Count;
                        if (!p.IsStatic)
                            ret++; //This arg
                        
                        ret++; //For MethodInfo arg
                        return ret;
                    }).ToArray();

                    // if (parameterCounts.Max() != parameterCounts.Min())
                        // throw new("Cannot handle call to address with multiple managed methods of different parameter counts");
                    
                    var parameterCount = parameterCounts.Max();
                    var registerParams = new[] {"rcx", "rdx", "r8", "r9"}.Select(InstructionSetIndependentOperand.MakeRegister).ToList();

                    if (parameterCount <= registerParams.Count)
                    {
                        builder.Call(instruction.IP, target, registerParams.GetRange(0, parameterCount).ToArray());
                        return;
                    }
                    
                    //Need to use stack
                    parameterCount -= registerParams.Count; //Subtract the 4 params we can fit in registers

                    //Generate and append stack operands
                    var ptrSize = (int) context.AppContext.Binary.PointerSize;
                    registerParams = registerParams.Concat(Enumerable.Range(0, parameterCount).Select(p => p * ptrSize).Select(InstructionSetIndependentOperand.MakeStack)).ToList();
                    
                    builder.Call(instruction.IP, target, registerParams.ToArray());
                    
                    //Discard the consumed stack space
                    builder.ShiftStack(instruction.IP, -parameterCount * 8);
                }
                else
                {
                    //This isn't a managed method, so for now we don't know its parameter count.
                    //Add all four of the registers, I guess. If there are any functions that take more than 4 params,
                    //we'll have to do something else here.
                    //These can be converted to dedicated ISIL instructions for specific API functions at a later stage. (by a post-processing step)
                    var paramRegisters = new[] {"rcx", "rdx", "r8", "r9"}.Select(InstructionSetIndependentOperand.MakeRegister).ToArray();
                    builder.Call(instruction.IP, target, paramRegisters);
                }
                break;
            case Mnemonic.Test:
                if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register && instruction.Op0Register == instruction.Op1Register)
                {
                    builder.Compare(instruction.IP, ConvertOperand(instruction, 0), InstructionSetIndependentOperand.MakeImmediate(0));
                    break;
                }
                goto default;
            case Mnemonic.Cmp:
                builder.Compare(instruction.IP, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Jmp:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget; 

                    var methodEnd = instruction.IP + (ulong) context.RawBytes.Length;
                    var methodStart = context.UnderlyingPointer;

                    if (jumpTarget < methodStart || jumpTarget > methodEnd)
                    {
                        goto case Mnemonic.Call; // This is like 99% likely a non returning call, jump to case to avoid code duplication 
                        // TODO: Add a return instruction after this or have a flag passed somehow to the Call case to use the CallNoReturn instruction?
                    }
                    else
                    {
                        builder.Goto(instruction.IP, jumpTarget);
                        break;
                    }
                }
                goto default;
            case Mnemonic.Je:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget;
                    
                    builder.JumpIfEqual(instruction.IP, jumpTarget);
                    break;
                }
                goto default;
            case Mnemonic.Jne:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget;
                    
                    builder.JumpIfNotEqual(instruction.IP, jumpTarget);
                    break;
                }
                goto default;
            case Mnemonic.Jg:
            case Mnemonic.Ja:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget;
                    
                    builder.JumpIfGreater(instruction.IP, jumpTarget);
                    break;
                }
                goto default;
            case Mnemonic.Jl:
            case Mnemonic.Jb:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget;
                    
                    builder.JumpIfLess(instruction.IP, jumpTarget);
                    break;
                }
                goto default;
            case Mnemonic.Jge:
            case Mnemonic.Jae:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget;
                    
                    builder.JumpIfGreaterOrEqual(instruction.IP, jumpTarget);
                    break;
                }
                goto default;
            case Mnemonic.Jle:
            case Mnemonic.Jbe:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget;
                    
                    builder.JumpIfLessOrEqual(instruction.IP, jumpTarget);
                    break;
                }
                goto default;  
            case Mnemonic.Int:
            case Mnemonic.Int3:
                builder.Interrupt(instruction.IP); // We'll add it but eliminate later
                break;
            default:
                builder.NotImplemented(instruction.IP, instruction.ToString());
                break;
        }
    }
    

    private InstructionSetIndependentOperand ConvertOperand(Instruction instruction, int operand)
    {
        var kind = instruction.GetOpKind(operand);

        if (kind == OpKind.Register)
            return InstructionSetIndependentOperand.MakeRegister(X86Utils.GetRegisterName(instruction.GetOpRegister(operand)));
        if (kind.IsImmediate())
            return InstructionSetIndependentOperand.MakeImmediate(instruction.GetImmediate(operand));
        if (kind == OpKind.Memory && instruction.MemoryBase == Register.RSP)
            return InstructionSetIndependentOperand.MakeStack((int) instruction.MemoryDisplacement32);

        //Memory
        //Most complex to least complex
        
        if(instruction.IsIPRelativeMemoryOperand)
            return InstructionSetIndependentOperand.MakeMemory(new(instruction.IPRelativeMemoryAddress));

        //All four components
        if (instruction.MemoryIndex != Register.None && instruction.MemoryBase != Register.None && instruction.MemoryDisplacement64 != 0)
        {
            var mBase = InstructionSetIndependentOperand.MakeRegister(X86Utils.GetRegisterName(instruction.MemoryBase));
            var mIndex = InstructionSetIndependentOperand.MakeRegister(X86Utils.GetRegisterName(instruction.MemoryIndex));
            return InstructionSetIndependentOperand.MakeMemory(new(mBase, mIndex, instruction.MemoryDisplacement32, instruction.MemoryIndexScale));
        }

        //No addend
        if (instruction.MemoryIndex != Register.None && instruction.MemoryBase != Register.None)
        {
            var mBase = InstructionSetIndependentOperand.MakeRegister(X86Utils.GetRegisterName(instruction.MemoryBase));
            var mIndex = InstructionSetIndependentOperand.MakeRegister(X86Utils.GetRegisterName(instruction.MemoryIndex));
            return InstructionSetIndependentOperand.MakeMemory(new(mBase, mIndex, instruction.MemoryIndexScale));
        }

        //No index (and so no scale)
        if (instruction.MemoryBase != Register.None && instruction.MemoryDisplacement64 > 0)
        {
            var mBase = InstructionSetIndependentOperand.MakeRegister(X86Utils.GetRegisterName(instruction.MemoryBase));
            return InstructionSetIndependentOperand.MakeMemory(new(mBase, instruction.MemoryDisplacement64));
        }

        //Only base
        if (instruction.MemoryBase != Register.None)
        {
            return InstructionSetIndependentOperand.MakeMemory(new(InstructionSetIndependentOperand.MakeRegister(X86Utils.GetRegisterName(instruction.MemoryBase))));
        }

        //Only addend
        return InstructionSetIndependentOperand.MakeMemory(new(instruction.MemoryDisplacement64));
    }
}