//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Cci;

namespace CSharpSourceEmitter {
  public partial class SourceEmitter : BaseCodeTraverser, ICSharpSourceEmitter {
    public override void Visit(IMethodDefinition methodDefinition) {
      if (methodDefinition.IsConstructor && methodDefinition.ParameterCount == 0 && 
        AttributeHelper.Contains(methodDefinition.Attributes, methodDefinition.Type.PlatformType.SystemRuntimeCompilerServicesCompilerGeneratedAttribute))
        return;

      // Skip if this is a method generated for use by a property or event
      foreach (var p in methodDefinition.ContainingTypeDefinition.Properties)
        if ((p.Getter != null && p.Getter.ResolvedMethod == methodDefinition) ||
          (p.Setter != null && p.Setter.ResolvedMethod == methodDefinition))
          return;
      foreach (var e in methodDefinition.ContainingTypeDefinition.Events)
        if ((e.Adder != null && e.Adder.ResolvedMethod == methodDefinition) ||
          (e.Remover != null && e.Remover.ResolvedMethod == methodDefinition))
          return;

      if (AttributeHelper.Contains(methodDefinition.Attributes, methodDefinition.Type.PlatformType.SystemRuntimeCompilerServicesCompilerGeneratedAttribute))
        return; // eg. an iterator helper - may have invalid identifier name

      // Cctors should probably be outputted in some cases
      if (methodDefinition.IsStaticConstructor) return;

      foreach (var ma in methodDefinition.Attributes)
        if (Utils.GetAttributeType(ma) != SpecialAttribute.Extension)
          PrintAttribute(methodDefinition, ma, true, null);

      foreach (var ra in methodDefinition.ReturnValueAttributes)
        PrintAttribute(methodDefinition, ra, true, "return");

      PrintToken(CSharpToken.Indent);

      PrintMethodDefinitionVisibility(methodDefinition);
      PrintMethodDefinitionModifiers(methodDefinition);

      bool conversion = IsConversionOperator(methodDefinition);
      if (!conversion) {
        PrintMethodDefinitionReturnType(methodDefinition);
        if (!methodDefinition.IsConstructor && !IsDestructor(methodDefinition))
          PrintToken(CSharpToken.Space);
      }
      PrintMethodDefinitionName(methodDefinition);
      if (conversion)
        PrintMethodDefinitionReturnType(methodDefinition);

      if (methodDefinition.IsGeneric) {
        Visit(methodDefinition.GenericParameters);
      }
      Visit(methodDefinition.Parameters);
      if (!methodDefinition.IsAbstract && !methodDefinition.IsExternal)
        Visit(methodDefinition.Body);
      else
        PrintToken(CSharpToken.Semicolon);
    }

    public virtual void PrintMethodDefinitionVisibility(IMethodDefinition methodDefinition) {
      if (!IsDestructor(methodDefinition) &&
        !methodDefinition.ContainingTypeDefinition.IsInterface &&
        IteratorHelper.EnumerableIsEmpty(MemberHelper.GetExplicitlyOverriddenMethods(methodDefinition)))
        PrintTypeMemberVisibility(methodDefinition.Visibility);
    }

    public virtual bool IsMethodUnsafe(IMethodDefinition methodDefinition) {
      foreach (var p in methodDefinition.Parameters) {
        if (p.Type.TypeCode == PrimitiveTypeCode.Pointer)
          return true;
      }
      if (methodDefinition.Type.TypeCode == PrimitiveTypeCode.Pointer)
        return true;
      return false;
    }

    public virtual bool IsOperator(IMethodDefinition methodDefinition) {
      return (methodDefinition.IsSpecialName && methodDefinition.Name.Value.StartsWith("op_"));
    }

    public virtual bool IsConversionOperator(IMethodDefinition methodDefinition) {
      return (methodDefinition.IsSpecialName && (
        methodDefinition.Name.Value == "op_Explicit" || methodDefinition.Name.Value == "op_Implicit"));
    }

    public virtual bool IsDestructor(IMethodDefinition methodDefinition) {

      if (methodDefinition.Name.Value == "Finalize" && methodDefinition.ParameterCount == 0)  // quick check
      {
        // Verify that this Finalize method override the public System.Object.Finalize
        var typeDef = methodDefinition.ContainingTypeDefinition;
        var objType = typeDef.PlatformType.SystemObject.ResolvedType;
        var finMethod = (IMethodDefinition)IteratorHelper.Single(
          objType.GetMatchingMembersNamed(methodDefinition.Name, false, m => m.Visibility == TypeMemberVisibility.Family));
        if (MemberHelper.GetImplicitlyOverridingDerivedClassMethod(finMethod, typeDef) != Dummy.Method)
          return true;
      }
      return false;
    }

    public virtual void PrintMethodDefinitionModifiers(IMethodDefinition methodDefinition) {

      // This algorithm is probably not exactly right yet.
      // TODO: Compare to FrameworkDesignStudio rules (see CCIModifiers project, and AssemblyDocumentWriter.WriteMemberStart)

      if (IsMethodUnsafe(methodDefinition))
        PrintKeywordUnsafe();

      if (Utils.GetHiddenBaseClassMethod(methodDefinition) != Dummy.Method)
        PrintKeywordNew();

      if (methodDefinition.ContainingTypeDefinition.IsInterface) {
        // Defining an interface method - 'unsafe' and 'new' are the only valid modifier
        return;
      }

      if (!methodDefinition.IsAbstract && methodDefinition.IsExternal)
        PrintKeywordExtern();

      if (IsDestructor(methodDefinition))
        return;

      if (methodDefinition.IsStatic) {
        PrintKeywordStatic();
      } else if (methodDefinition.IsVirtual) {
        if (methodDefinition.IsNewSlot && 
          (IteratorHelper.EnumerableIsNotEmpty(MemberHelper.GetImplicitlyImplementedInterfaceMethods(methodDefinition)) ||
            IteratorHelper.EnumerableIsNotEmpty(MemberHelper.GetExplicitlyOverriddenMethods(methodDefinition)))) {
          // Implementing a method defined on an interface: implicitly virtual and sealed
          if (methodDefinition.IsAbstract)
            PrintKeywordAbstract();
          else if (!methodDefinition.IsSealed)
            PrintKeywordVirtual();
        } else {
          // Instance method on a class
          if (methodDefinition.IsAbstract)
            PrintKeywordAbstract();

          if (methodDefinition.IsNewSlot) {
            // Only overrides (or interface impls) can be sealed in C#.  If this is
            // a new sealed virtual then just emit as non-virtual which is a similar thing.
            // We get these in reference assemblies for methods which were implementations of private (and so removed)
            // interfaces.
            if (!methodDefinition.IsSealed && !methodDefinition.IsAbstract)
              PrintKeywordVirtual();
          } else {
            PrintKeywordOverride();
            if (methodDefinition.IsSealed)
              PrintKeywordSealed();
          }
        }
      }
    }

    public virtual void PrintMethodDefinitionReturnType(IMethodDefinition methodDefinition) {
      if (!methodDefinition.IsConstructor && !IsDestructor(methodDefinition) /*&& !IsOperator(methodDefinition)*/)
        PrintTypeReference(methodDefinition.Type);
    }

    public virtual void PrintMethodDefinitionName(IMethodDefinition methodDefinition) {
      bool isDestructor = IsDestructor(methodDefinition);
      if (isDestructor)
        PrintToken(CSharpToken.Tilde);
      if (methodDefinition.IsConstructor || isDestructor)
        PrintTypeDefinitionName(methodDefinition.ContainingTypeDefinition);
      else if (IsOperator(methodDefinition)) {
        sourceEmitterOutput.Write(MapOperatorNameToCSharp(methodDefinition));
      } else
        PrintIdentifier(methodDefinition.Name);
    }

    public virtual string MapOperatorNameToCSharp(IMethodDefinition methodDefinition) {
      // ^ requires IsOperator(methodDefinition)
      switch (methodDefinition.Name.Value) {
        case "op_Decrement": return "operator --";
        case "op_Increment": return "operator ++";
        case "op_UnaryNegation": return "operator -";
        case "op_UnaryPlus": return "operator +";
        case "op_LogicalNot": return "operator !";
        case "op_OnesComplement": return "operator ~";
        case "op_True": return "operator true";
        case "op_False": return "operator false";
        case "op_Addition": return "operator +";
        case "op_Subtraction": return "operator -";
        case "op_Multiply": return "operator *";
        case "op_Division": return "operator /";
        case "op_Modulus": return "operator %";
        case "op_ExclusiveOr": return "operator ^";
        case "op_BitwiseAnd": return "operator &";
        case "op_BitwiseOr": return "operator |";
        case "op_LeftShift": return "operator <<";
        case "op_RightShift": return "operator >>";
        case "op_Equality": return "operator ==";
        case "op_GreaterThan": return "operator >";
        case "op_LessThan": return "operator <";
        case "op_Inequality": return "operator !=";
        case "op_GreaterThanOrEqual": return "operator >=";
        case "op_LessThanOrEqual": return "operator <=";
        case "op_Explicit": return "explicit operator ";
        case "op_Implicit": return "implicit operator ";
        default: return methodDefinition.Name.Value; // other unsupported by C# directly
      }
    }
    public virtual void PrintMethodReferenceName(IMethodReference methodReference, NameFormattingOptions options) {
      string signature = MemberHelper.GetMethodSignature(methodReference, options|NameFormattingOptions.ContractNullable|NameFormattingOptions.UseTypeKeywords);
      if (methodReference.Name.Value == ".ctor")
        PrintTypeReferenceName(methodReference.ContainingType);
      else
        sourceEmitterOutput.Write(signature);
    }

    public override void Visit(IMethodBody methodBody) {
      PrintToken(CSharpToken.LeftCurly);

      base.Visit(methodBody);

      PrintToken(CSharpToken.RightCurly);
    }

  }
}