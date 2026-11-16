using SmartX.Api.Models;

namespace SmartX.Api.Services;

public class DeploymentValidator
{
    private const int MaxDepth = 6;

    public List<ValidationIssue> Validate(DeploymentNode root) => Validate(root, MaxDepth);

    // Method overload: same name, an extra parameter. Lets a caller tighte or loosen the depth limit for a particular site without changing the
    // default used everywhere else.
    public List<ValidationIssue> Validate(DeploymentNode root, int maxDepth)
    {
        var issues = new List<ValidationIssue>();
        var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(root, root.Name, 0, maxDepth, seenMacs, issues);
        return issues;
    }

    private void Walk(DeploymentNode node, string path, int depth, int maxDepth, HashSet<string> seenMacs, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
        {
            issues.Add(new ValidationIssue(path, "Node has no name."));
        }

        // base case: stop recursing once a branch gets unreasonably deep
        if (depth > maxDepth)
        {
            issues.Add(new ValidationIssue(path, $"Tree exceeds max depth of {maxDepth}."));
            return;
        }

        if (node.DeviceMac is not null && !seenMacs.Add(node.DeviceMac))
        {
            issues.Add(new ValidationIssue(path, $"Duplicate MAC address '{node.DeviceMac}' appears more than once in the tree."));
        }

        foreach (var child in node.Children)
        {
            Walk(child, $"{path} -> {child.Name}", depth + 1, maxDepth, seenMacs, issues);
        }
    }
}
