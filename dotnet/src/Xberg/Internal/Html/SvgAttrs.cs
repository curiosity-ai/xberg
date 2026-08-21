namespace Xberg.Internal.Html;

/// <summary>
/// Canonical WHATWG case for the SVG and MathML attributes whose spelling is not all-lowercase
/// (`html-to-markdown-rs` `converter/utility/svg_attrs.rs`).
/// </summary>
/// <remarks>
/// A parser that lowercases attribute names would otherwise turn <c>viewBox</c> into
/// <c>viewbox</c> in the serialized SVG the markdown carries as a data URI. Only mixed-case
/// names are listed; everything else needs no substitution.
/// </remarks>
internal static class SvgAttrs
{
    private static readonly Dictionary<string, string> Camel = new(StringComparer.Ordinal)
    {
        ["attributename"] = "attributeName",
        ["attributetype"] = "attributeType",
        ["basefrequency"] = "baseFrequency",
        ["calcmode"] = "calcMode",
        ["clippath"] = "clipPath",
        ["clippathunits"] = "clipPathUnits",
        ["diffuseconstant"] = "diffuseConstant",
        ["edgemode"] = "edgeMode",
        ["filterunits"] = "filterUnits",
        ["gradienttransform"] = "gradientTransform",
        ["gradientunits"] = "gradientUnits",
        ["kernelmatrix"] = "kernelMatrix",
        ["kernelunitlength"] = "kernelUnitLength",
        ["keypoints"] = "keyPoints",
        ["keysplines"] = "keySplines",
        ["keytimes"] = "keyTimes",
        ["lengthadjust"] = "lengthAdjust",
        ["limitingconeangle"] = "limitingConeAngle",
        ["markerheight"] = "markerHeight",
        ["markerunits"] = "markerUnits",
        ["markerwidth"] = "markerWidth",
        ["maskunits"] = "maskUnits",
        ["maskcontentunits"] = "maskContentUnits",
        ["numoctaves"] = "numOctaves",
        ["pathlength"] = "pathLength",
        ["patterncontentunits"] = "patternContentUnits",
        ["patterntransform"] = "patternTransform",
        ["patternunits"] = "patternUnits",
        ["pointsatx"] = "pointsAtX",
        ["pointsaty"] = "pointsAtY",
        ["pointsatz"] = "pointsAtZ",
        ["preserveaspectratio"] = "preserveAspectRatio",
        ["primitiveunits"] = "primitiveUnits",
        ["refx"] = "refX",
        ["refy"] = "refY",
        ["repeatcount"] = "repeatCount",
        ["repeatdur"] = "repeatDur",
        ["specularconstant"] = "specularConstant",
        ["specularexponent"] = "specularExponent",
        ["spreadmethod"] = "spreadMethod",
        ["startoffset"] = "startOffset",
        ["stddeviation"] = "stdDeviation",
        ["stitchtiles"] = "stitchTiles",
        ["surfacescale"] = "surfaceScale",
        ["systemlanguage"] = "systemLanguage",
        ["tablevalues"] = "tableValues",
        ["targetx"] = "targetX",
        ["targety"] = "targetY",
        ["textlength"] = "textLength",
        ["viewbox"] = "viewBox",
        ["xchannelselector"] = "xChannelSelector",
        ["ychannelselector"] = "yChannelSelector",
        ["zoomandpan"] = "zoomAndPan",
    };

    /// <summary>The canonical spelling for an all-lowercase key, or null when none applies.</summary>
    public static string? Canonical(string key) => Camel.TryGetValue(key, out var canonical) ? canonical : null;
}
